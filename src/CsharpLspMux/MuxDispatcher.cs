using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

public sealed class MuxDispatcher
{
    private readonly ISolutionRouter _router;
    private readonly IServerPool<IChildServer> _pool;
    private readonly IFrameWriter _transport;
    private readonly Func<string, Task<string>> _readFile;
    private readonly MuxLogger? _logger;
    private readonly RequestLedger _ledger = new();
    private readonly OpenFileTracker _tracker = new();
    private bool _poolDrained;

    public MuxDispatcher(
        ISolutionRouter router,
        IServerPool<IChildServer> pool,
        IFrameWriter transport,
        Func<string, Task<string>>? readFile = null,
        MuxLogger? logger = null)
    {
        _router = router;
        _pool = pool;
        _transport = transport;
        _readFile = readFile ?? (path => File.ReadAllTextAsync(path));
        _logger = logger;
        pool.OnEviction = s => { NotifyEviction(s); return Task.CompletedTask; };
    }

    public Task<bool> HandleMessageAsync(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        return method switch
        {
            "initialize" => HandleInitialize(message),
            "initialized" => Task.FromResult(true),
            "$/cancelRequest" => HandleCancelRequest(message),
            "workspace/symbol" => HandleWorkspaceSymbol(message),
            "workspace/didChangeConfiguration" => HandleDidChangeConfiguration(message),
            "workspace/didChangeWatchedFiles" => HandleDidChangeWatchedFiles(message),
            "shutdown" => HandleShutdown(message),
            "exit" => HandleExit(message),
            _ when method?.StartsWith("textDocument/", StringComparison.Ordinal) == true => HandleTextDocument(message),
            _ => Task.FromResult(true),
        };
    }

    private async Task<bool> HandleInitialize(JsonObject message)
    {
        var id = message["id"];
        await _transport.SendResponseAsync(id, new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 2 },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["referencesProvider"] = true,
                ["documentSymbolProvider"] = true,
                ["workspaceSymbolProvider"] = true,
                ["completionProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray(".", " ")
                },
                ["signatureHelpProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray("(", ",")
                },
                ["renameProvider"] = true,
                ["codeActionProvider"] = true,
                ["diagnosticProvider"] = new JsonObject
                {
                    ["interFileDependencies"] = true,
                    ["workspaceDiagnostics"] = false
                }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "csharp-lsp-mux",
                ["version"] = "0.1.0"
            }
        });
        return true;
    }

    private async Task<bool> HandleTextDocument(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var uri = message["params"]?["textDocument"]?["uri"]?.GetValue<string>();
        if (uri is not null
            && Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri)
            && parsedUri.IsFile)
        {
            var filePath = parsedUri.LocalPath;
            var solutionPath = _router.Route(filePath);

            if (solutionPath is not null)
            {
                var server = await _pool.GetOrAddAsync(solutionPath);

                if (_logger?.IsEnabled == true)
                {
                    var state = server.Readiness.ToString().ToLowerInvariant();
                    _logger.Log($"[mux] route {method} → {solutionPath} (server: {state})");
                }

                var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                if (method == "textDocument/didClose")
                {
                    _tracker.MarkClosed(server, uri);
                    await server.ForwardNotificationAsync(raw);
                }
                else if (method == "textDocument/didOpen")
                {
                    _tracker.MarkOpened(server, uri);
                    await server.ForwardNotificationAsync(raw);
                }
                else
                {
                    if (message["id"] is not null && !_tracker.IsOpened(server, uri))
                        await EnsureOpenAsync(server, uri, filePath);
                    if (message["id"] is JsonNode requestId)
                        _ledger.Register(JsonNodeToKey(requestId), server);
                    await server.ForwardRequestAsync(raw);
                }
            }
            else
            {
                _logger?.Log($"[mux] SolutionRouter: no solution found for {filePath}");
                if (message["id"] is JsonNode requestId)
                    await _transport.SendErrorAsync(requestId, -32001, $"No solution found for file: {filePath}");
            }
        }
        return true;
    }

    private async Task EnsureOpenAsync(IChildServer server, string uri, string filePath)
    {
        string text;
        try { text = await _readFile(filePath); }
        catch (Exception) { text = ""; }

        var didOpen = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/didOpen",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = text
                }
            }
        };
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(didOpen));
        await server.ForwardNotificationAsync(raw);
        _tracker.MarkOpened(server, uri);
    }

    private async Task<bool> HandleCancelRequest(JsonObject message)
    {
        var cancelId = message["params"]?["id"];
        if (cancelId is not null)
        {
            var key = JsonNodeToKey(cancelId);
            var owner = _ledger.Lookup(key);
            if (owner is not null)
            {
                _ledger.Remove(key);
                var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                await owner.ForwardRequestAsync(raw);
            }
        }
        return true;
    }

    private async Task<bool> HandleWorkspaceSymbol(JsonObject message)
    {
        var requestId = message["id"];
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var servers = _pool.ActiveServers.ToList();

        if (servers.Count == 0)
        {
            if (requestId is not null)
                await _transport.SendResponseAsync(requestId, new JsonArray());
        }
        else
        {
            var responses = await Task.WhenAll(servers.Select(async s =>
            {
                try { return await s.SendAndReceiveAsync(raw); }
                catch { return null; }
            }));
            var merged = new JsonArray();
            foreach (var resp in responses)
            {
                if (resp is null) continue;
                try
                {
                    var parsed = JsonSerializer.Deserialize<JsonObject>(resp);
                    if (parsed?["result"] is JsonArray arr)
                        foreach (var item in arr)
                            merged.Add(item?.DeepClone());
                }
                catch (JsonException) { }
            }
            if (requestId is not null)
                await _transport.SendResponseAsync(requestId, merged);
        }
        return true;
    }

    private async Task<bool> HandleDidChangeConfiguration(JsonObject message)
    {
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        foreach (var server in _pool.ActiveServers.ToList())
            await server.ForwardRequestAsync(raw);
        return true;
    }

    private Task<bool> HandleDidChangeWatchedFiles(JsonObject message)
    {
        var changes = message["params"]?["changes"]?.AsArray();
        if (changes is not null)
        {
            foreach (var change in changes)
            {
                var uriStr = change?["uri"]?.GetValue<string>();
                if (uriStr is null) continue;
                if (!Uri.TryCreate(uriStr, UriKind.Absolute, out var parsedUri) || !parsedUri.IsFile)
                    continue;
                var path = parsedUri.LocalPath;
                var ext = Path.GetExtension(path);
                if (ext is ".sln" or ".slnx" or ".csproj")
                    _router.NotifyFileChanged(path);
            }
        }
        return Task.FromResult(true);
    }

    private async Task<bool> HandleShutdown(JsonObject message)
    {
        var id = message["id"];
        await _pool.DisposeAllAsync();
        _poolDrained = true;
        await _transport.SendResponseAsync(id, JsonValue.Create<object?>(null)!);
        return true;
    }

    private async Task<bool> HandleExit(JsonObject message)
    {
        if (!_poolDrained)
            await _pool.DisposeAllAsync();
        return false;
    }

    private void NotifyEviction(IChildServer evicted)
    {
        _ledger.EvictServer(evicted);
        _tracker.EvictServer(evicted);
    }

    private static string JsonNodeToKey(JsonNode node) => node.ToJsonString();
}
