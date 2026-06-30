using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

public sealed class MuxDispatcher(
    ISolutionRouter router,
    IServerPool<IChildServer> pool,
    ILspTransport transport)
{
    private readonly Dictionary<string, IChildServer> _requestOwners = new();
    private bool _poolDrained;

    public async Task<bool> HandleMessageAsync(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();

        if (method == "initialize")
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

        if (method == "initialized")
            return true;

        if (method is not null && method.StartsWith("textDocument/", StringComparison.Ordinal))
        {
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

        if (method == "$/cancelRequest")
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

        if (method == "workspace/symbol")
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

        if (method == "workspace/didChangeWatchedFiles")
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
            return true;
        }

        if (method == "shutdown")
        {
            var id = message["id"];
            await pool.DisposeAllAsync();
            _poolDrained = true;
            await transport.SendResponseAsync(id, JsonValue.Create<object?>(null)!);
            return true;
        }

        if (method == "exit")
        {
            if (!_poolDrained)
                await pool.DisposeAllAsync();
            return false;
        }

        return true;
    }

    public void NotifyEviction(IChildServer evicted)
    {
        foreach (var key in _requestOwners.Keys.Where(k => _requestOwners[k] == evicted).ToList())
            _requestOwners.Remove(key);
    }

    private static string JsonNodeToKey(JsonNode node) => node.ToJsonString();
}
