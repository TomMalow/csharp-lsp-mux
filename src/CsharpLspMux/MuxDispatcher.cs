using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

public sealed class MuxDispatcher(
    ISolutionRouter router,
    IServerPool<IChildServer> pool,
    ILspTransport transport,
    Func<string, Task<string>>? readFile = null)
{
    private readonly Func<string, Task<string>> _readFile = readFile ?? (path => File.ReadAllTextAsync(path));
    private readonly Dictionary<string, IChildServer> _requestOwners = new();
    private readonly Dictionary<IChildServer, HashSet<string>> _openedUris = new();
    private bool _poolDrained;

    public Task<bool> HandleMessageAsync(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        return method switch
        {
            "initialize" => HandleInitialize(message),
            "initialized" => Task.FromResult(true),
            "$/cancelRequest" => HandleCancelRequest(message),
            "workspace/symbol" => HandleWorkspaceSymbol(message),
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
        await transport.SendResponseAsync(id, new JsonObject
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
            var solutionPath = router.Route(filePath);

            if (solutionPath is not null)
            {
                var server = await pool.GetOrAddAsync(solutionPath);

                if (method == "textDocument/didClose")
                    MarkClosed(server, uri);
                else if (method == "textDocument/didOpen")
                    MarkOpened(server, uri);
                else if (message["id"] is not null && !IsOpened(server, uri))
                    await EnsureOpenAsync(server, uri, filePath);

                var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                if (message["id"] is JsonNode requestId)
                    _requestOwners[JsonNodeToKey(requestId)] = server;
                await server.ForwardRequestAsync(raw);
            }
            else
            {
                if (message["id"] is JsonNode requestId)
                    await transport.SendResponseAsync(requestId, JsonValue.Create<object?>(null)!);
            }
        }
        return true;
    }

    private void MarkOpened(IChildServer server, string uri)
    {
        if (!_openedUris.TryGetValue(server, out var set))
            _openedUris[server] = set = new HashSet<string>();
        set.Add(uri);
    }

    private void MarkClosed(IChildServer server, string uri)
    {
        if (_openedUris.TryGetValue(server, out var set))
            set.Remove(uri);
    }

    private bool IsOpened(IChildServer server, string uri) =>
        _openedUris.TryGetValue(server, out var set) && set.Contains(uri);

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
        await server.ForwardRequestAsync(raw);
        MarkOpened(server, uri);
    }

    private async Task<bool> HandleCancelRequest(JsonObject message)
    {
        var cancelId = message["params"]?["id"];
        if (cancelId is not null)
        {
            var key = JsonNodeToKey(cancelId);
            if (_requestOwners.TryGetValue(key, out var owner))
            {
                _requestOwners.Remove(key);
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
        var servers = pool.ActiveServers.ToList();

        if (servers.Count == 0)
        {
            if (requestId is not null)
                await transport.SendResponseAsync(requestId, new JsonArray());
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
                await transport.SendResponseAsync(requestId, merged);
        }
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
                    router.InvalidateCache(path);
            }
        }
        return Task.FromResult(true);
    }

    private async Task<bool> HandleShutdown(JsonObject message)
    {
        var id = message["id"];
        await pool.DisposeAllAsync();
        _poolDrained = true;
        await transport.SendResponseAsync(id, JsonValue.Create<object?>(null)!);
        return true;
    }

    private async Task<bool> HandleExit(JsonObject message)
    {
        if (!_poolDrained)
            await pool.DisposeAllAsync();
        return false;
    }

    public void NotifyEviction(IChildServer evicted)
    {
        foreach (var key in _requestOwners.Keys.Where(k => _requestOwners[k] == evicted).ToList())
            _requestOwners.Remove(key);
        _openedUris.Remove(evicted);
    }

    private static string JsonNodeToKey(JsonNode node) => node.ToJsonString();
}
