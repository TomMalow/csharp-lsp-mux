using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class MuxDispatcherTests
{
    // --- fakes ---

    private sealed class FakeTransport : ILspTransport
    {
        public readonly List<byte[]> WrittenFrames = new();
        public readonly List<(JsonNode? Id, JsonNode Result)> Responses = new();
        public readonly List<(JsonNode? Id, int Code, string Message)> Errors = new();

        public Task WriteFrameAsync(byte[] frame) { WrittenFrames.Add(frame); return Task.CompletedTask; }
        public Task SendResponseAsync(JsonNode? id, JsonNode result) { Responses.Add((id, result)); return Task.CompletedTask; }
        public Task SendErrorAsync(JsonNode? id, int code, string message) { Errors.Add((id, code, message)); return Task.CompletedTask; }
    }

    private sealed class FakeRouter : ISolutionRouter
    {
        public string? RouteResult { get; set; }
        public readonly List<string> InvalidatedPaths = new();

        public string? Route(string absoluteFilePath) => RouteResult;
        public void InvalidateCache(string changedPath) => InvalidatedPaths.Add(changedPath);
    }

    private sealed class FakeServer : IChildServer
    {
        public int DisposeCount;
        public readonly List<byte[]> ForwardedFrames = new();
        public Func<byte[], byte[]>? SendAndReceiveHandler { get; set; }

        public Task ForwardRequestAsync(byte[] frame) { ForwardedFrames.Add(frame); return Task.CompletedTask; }

        public Task<byte[]> SendAndReceiveAsync(byte[] frame)
        {
            if (SendAndReceiveHandler is not null)
            {
                try { return Task.FromResult(SendAndReceiveHandler(frame)); }
                catch (Exception ex) { return Task.FromException<byte[]>(ex); }
            }
            var req = JsonSerializer.Deserialize<JsonObject>(frame)!;
            var id = req["id"];
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new JsonArray()
            };
            return Task.FromResult(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp)));
        }

        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class FakeServerPool : IServerPool<IChildServer>
    {
        private readonly Dictionary<string, IChildServer> _servers = new();

        public Task<IChildServer> GetOrAddAsync(string key)
        {
            if (!_servers.TryGetValue(key, out var server))
            {
                server = new FakeServer();
                _servers[key] = server;
            }
            return Task.FromResult(server);
        }

        public IEnumerable<IChildServer> ActiveServers => _servers.Values;

        public Task DisposeAllAsync() => Task.CompletedTask;
    }

    // --- helpers ---

    private static (MuxDispatcher dispatcher, FakeTransport transport, FakeRouter router, FakeServerPool pool)
        Make(string? routeResult = null, Func<string, Task<string>>? readFile = null)
    {
        var transport = new FakeTransport();
        var router = new FakeRouter { RouteResult = routeResult };
        var pool = new FakeServerPool();
        var dispatcher = new MuxDispatcher(router, pool, transport, readFile);
        return (dispatcher, transport, router, pool);
    }

    private static JsonObject Msg(string method, JsonNode? id = null, JsonObject? @params = null)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) o["id"] = id.DeepClone();
        if (@params is not null) o["params"] = @params.DeepClone();
        return o;
    }

    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    // --- tests ---

    [Fact]
    public async Task Initialize_SendsCapabilitiesResponse_ReturnsTrue()
    {
        var (dispatcher, transport, _, _) = Make();
        var msg = Msg("initialize", id: JsonValue.Create(1));

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Single(transport.Responses);
        var (id, resp) = transport.Responses[0];
        Assert.Equal(1, id?.GetValue<int>());
        Assert.NotNull(resp["capabilities"]);
        Assert.Equal("csharp-lsp-mux", resp["serverInfo"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Initialize_CapabilitiesContainRoslynSuperset()
    {
        var (dispatcher, transport, _, _) = Make();
        var msg = Msg("initialize", id: JsonValue.Create(1));

        await dispatcher.HandleMessageAsync(msg);

        var caps = transport.Responses[0].Result["capabilities"] as JsonObject;
        Assert.NotNull(caps);
        Assert.True(caps["hoverProvider"]?.GetValue<bool>());
        Assert.True(caps["definitionProvider"]?.GetValue<bool>());
        Assert.True(caps["referencesProvider"]?.GetValue<bool>());
        Assert.True(caps["documentSymbolProvider"]?.GetValue<bool>());
        Assert.True(caps["workspaceSymbolProvider"]?.GetValue<bool>());
        Assert.True(caps["renameProvider"]?.GetValue<bool>());
        Assert.True(caps["codeActionProvider"]?.GetValue<bool>());
        Assert.True(caps["textDocumentSync"]?["openClose"]?.GetValue<bool>());
        Assert.Equal(2, caps["textDocumentSync"]?["change"]?.GetValue<int>());
        var completionTriggers = caps["completionProvider"]!["triggerCharacters"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains(".", completionTriggers);
        var signatureTriggers = caps["signatureHelpProvider"]!["triggerCharacters"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains("(", signatureTriggers);
        Assert.True(caps["diagnosticProvider"]?["interFileDependencies"]?.GetValue<bool>());
        Assert.False(caps["diagnosticProvider"]?["workspaceDiagnostics"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Initialized_NoResponse_ReturnsTrue()
    {
        var (dispatcher, transport, _, _) = Make();

        var result = await dispatcher.HandleMessageAsync(Msg("initialized"));

        Assert.True(result);
        Assert.Empty(transport.Responses);
        Assert.Empty(transport.WrittenFrames);
    }

    [Fact]
    public async Task TextDocument_RoutableUri_ForwardsToServer_ReturnsTrue()
    {
        var sln = "/repo/App.slnx";
        var (dispatcher, transport, _, pool) = Make(routeResult: sln, readFile: _ => Task.FromResult(""));
        var server = (FakeServer)await pool.GetOrAddAsync(sln);

        var msg = Msg("textDocument/hover", id: JsonValue.Create(2),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri("/repo/src/Foo.cs") } });

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Equal(2, server.ForwardedFrames.Count); // synthesized didOpen + hover
        Assert.Empty(transport.Responses);
    }

    [Fact]
    public async Task TextDocument_RequestWithId_NoSolution_SendsErrorResponse()
    {
        var (dispatcher, transport, _, _) = Make(routeResult: null);
        var msg = Msg("textDocument/hover", id: JsonValue.Create(3),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri("/repo/src/Foo.cs") } });

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Empty(transport.Responses);
        Assert.Single(transport.Errors);
        var (id, code, message) = transport.Errors[0];
        Assert.Equal(3, id?.GetValue<int>());
        Assert.Equal(-32001, code);
        Assert.Contains("Foo.cs", message);
    }

    [Fact]
    public async Task TextDocument_NotificationNoId_NoSolution_NoResponseSent()
    {
        var (dispatcher, transport, _, _) = Make(routeResult: null);
        var msg = Msg("textDocument/didOpen",
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri("/repo/src/Foo.cs") } });

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Empty(transport.Responses);
        Assert.Empty(transport.WrittenFrames);
    }

    [Fact]
    public async Task CancelRequest_ForwardsToOwner_RemovesFromOwners_ReturnsTrue()
    {
        var sln = "/repo/App.slnx";
        var (dispatcher, transport, _, pool) = Make(routeResult: sln, readFile: _ => Task.FromResult(""));
        var server = (FakeServer)await pool.GetOrAddAsync(sln);

        // Register a textDocument request so requestOwners has an entry
        var textDocMsg = Msg("textDocument/hover", id: JsonValue.Create(5),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri("/repo/src/Foo.cs") } });
        await dispatcher.HandleMessageAsync(textDocMsg);
        server.ForwardedFrames.Clear();

        var cancelMsg = Msg("$/cancelRequest",
            @params: new JsonObject { ["id"] = JsonValue.Create(5) });
        var result = await dispatcher.HandleMessageAsync(cancelMsg);

        Assert.True(result);
        Assert.Single(server.ForwardedFrames);

        // Cancelling again should NOT forward (entry removed)
        server.ForwardedFrames.Clear();
        await dispatcher.HandleMessageAsync(cancelMsg);
        Assert.Empty(server.ForwardedFrames);
    }

    [Fact]
    public async Task WorkspaceSymbol_NoActiveServers_SendsEmptyArray()
    {
        var (dispatcher, transport, _, _) = Make();
        var msg = Msg("workspace/symbol", id: JsonValue.Create(6),
            @params: new JsonObject { ["query"] = "Foo" });

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Single(transport.Responses);
        Assert.IsType<JsonArray>(transport.Responses[0].Result);
        Assert.Empty((JsonArray)transport.Responses[0].Result);
    }

    [Fact]
    public async Task WorkspaceSymbol_ActiveServers_BroadcastsMergesResults()
    {
        var sln1 = "/repo/A.slnx";
        var sln2 = "/repo/B.slnx";
        var transport = new FakeTransport();
        var router = new FakeRouter();

        static byte[] MakeSymbolResponse(string symbolName)
        {
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonValue.Create(7),
                ["result"] = new JsonArray { new JsonObject { ["name"] = symbolName } }
            };
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp));
        }

        var serverA = new FakeServer { SendAndReceiveHandler = _ => MakeSymbolResponse("Alpha") };
        var serverB = new FakeServer { SendAndReceiveHandler = _ => MakeSymbolResponse("Beta") };
        var pool = new ServerPool<IChildServer>(10, key =>
            Task.FromResult<IChildServer>(key == sln1 ? serverA : serverB));
        var dispatcher = new MuxDispatcher(router, pool, transport);
        pool.OnEvict = dispatcher.NotifyEviction;

        await pool.GetOrAddAsync(sln1);
        await pool.GetOrAddAsync(sln2);

        var msg = Msg("workspace/symbol", id: JsonValue.Create(7),
            @params: new JsonObject { ["query"] = "Al" });

        await dispatcher.HandleMessageAsync(msg);

        Assert.Single(transport.Responses);
        var merged = (JsonArray)transport.Responses[0].Result;
        Assert.Equal(2, merged.Count);
        var names = merged.Select(n => n?["name"]?.GetValue<string>()).ToHashSet();
        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
    }

    [Fact]
    public async Task WorkspaceDidChangeWatchedFiles_SlnChange_CallsInvalidateCache()
    {
        var (dispatcher, _, router, _) = Make();
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "workspace/didChangeWatchedFiles",
            ["params"] = new JsonObject
            {
                ["changes"] = new JsonArray
                {
                    new JsonObject { ["uri"] = FileUri("/repo/App.sln"), ["type"] = JsonValue.Create(2) }
                }
            }
        };

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Single(router.InvalidatedPaths);
        Assert.EndsWith("App.sln", router.InvalidatedPaths[0]);
    }

    [Fact]
    public async Task Shutdown_DrainsPool_ReturnsTrue()
    {
        var sln = "/repo/App.slnx";
        var transport = new FakeTransport();
        var router = new FakeRouter { RouteResult = sln };
        var server = new FakeServer();
        var pool = new ServerPool<IChildServer>(10, _ => Task.FromResult<IChildServer>(server));
        var dispatcher = new MuxDispatcher(router, pool, transport);

        await pool.GetOrAddAsync(sln);
        var msg = Msg("shutdown", id: JsonValue.Create(8));

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Equal(1, server.DisposeCount);
        Assert.Single(transport.Responses);
        Assert.Equal(8, transport.Responses[0].Id?.GetValue<int>());
    }

    [Fact]
    public async Task Exit_DrainsPool_ReturnsFalse()
    {
        var sln = "/repo/App.slnx";
        var transport = new FakeTransport();
        var router = new FakeRouter { RouteResult = sln };
        var server = new FakeServer();
        var pool = new ServerPool<IChildServer>(10, _ => Task.FromResult<IChildServer>(server));
        var dispatcher = new MuxDispatcher(router, pool, transport);

        await pool.GetOrAddAsync(sln);
        var result = await dispatcher.HandleMessageAsync(Msg("exit"));

        Assert.False(result);
        Assert.Equal(1, server.DisposeCount);
    }

    [Fact]
    public async Task Exit_AfterShutdown_DoesNotDrainTwice()
    {
        var sln = "/repo/App.slnx";
        var transport = new FakeTransport();
        var router = new FakeRouter { RouteResult = sln };
        var server = new FakeServer();
        var pool = new ServerPool<IChildServer>(10, _ => Task.FromResult<IChildServer>(server));
        var dispatcher = new MuxDispatcher(router, pool, transport);

        await pool.GetOrAddAsync(sln);
        await dispatcher.HandleMessageAsync(Msg("shutdown", id: JsonValue.Create(0)));
        var result = await dispatcher.HandleMessageAsync(Msg("exit"));

        Assert.False(result);
        Assert.Equal(1, server.DisposeCount); // only one dispose
    }

    [Fact]
    public async Task TextDocument_NonFileUri_NoResponseNoForward_ReturnsTrue()
    {
        var (dispatcher, transport, _, _) = Make(routeResult: "/repo/App.slnx");
        var msg = Msg("textDocument/hover", id: JsonValue.Create(10),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = "untitled:Untitled-1" } });

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Empty(transport.Responses);
    }

    [Fact]
    public async Task WorkspaceDidChangeWatchedFiles_NonFileUri_DoesNotInvalidate()
    {
        var (dispatcher, _, router, _) = Make();
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "workspace/didChangeWatchedFiles",
            ["params"] = new JsonObject
            {
                ["changes"] = new JsonArray
                {
                    new JsonObject { ["uri"] = "untitled:Untitled-1", ["type"] = JsonValue.Create(1) }
                }
            }
        };

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Empty(router.InvalidatedPaths);
    }

    [Fact]
    public async Task WorkspaceSymbol_FailingServer_ReturnsPartialResults()
    {
        var sln = "/repo/App.slnx";
        var transport = new FakeTransport();
        var router = new FakeRouter { RouteResult = sln };
        var server = new FakeServer
        {
            SendAndReceiveHandler = _ => throw new InvalidOperationException("server dead")
        };
        var pool = new ServerPool<IChildServer>(10, _ => Task.FromResult<IChildServer>(server));
        var dispatcher = new MuxDispatcher(router, pool, transport);

        await pool.GetOrAddAsync(sln);
        var msg = Msg("workspace/symbol", id: JsonValue.Create(11),
            @params: new JsonObject { ["query"] = "Foo" });

        var result = await dispatcher.HandleMessageAsync(msg);

        Assert.True(result);
        Assert.Single(transport.Responses);
        Assert.IsType<JsonArray>(transport.Responses[0].Result);
    }

    [Fact]
    public async Task TextDocument_RequestWithId_DidOpenNotSent_SynthesizesDidOpenFirst()
    {
        var sln = "/repo/App.slnx";
        var filePath = "/repo/src/Foo.cs";
        var fileContent = "class Foo {}";
        var readFileCalls = new List<string>();
        var (dispatcher, _, _, pool) = Make(
            routeResult: sln,
            readFile: path => { readFileCalls.Add(path); return Task.FromResult(fileContent); });
        var server = (FakeServer)await pool.GetOrAddAsync(sln);

        var msg = Msg("textDocument/hover", id: JsonValue.Create(2),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri(filePath) } });
        await dispatcher.HandleMessageAsync(msg);

        Assert.Equal(2, server.ForwardedFrames.Count);
        var didOpen = JsonSerializer.Deserialize<JsonObject>(server.ForwardedFrames[0])!;
        Assert.Equal("textDocument/didOpen", didOpen["method"]?.GetValue<string>());
        Assert.Equal(FileUri(filePath), didOpen["params"]?["textDocument"]?["uri"]?.GetValue<string>());
        Assert.Equal("csharp", didOpen["params"]?["textDocument"]?["languageId"]?.GetValue<string>());
        Assert.Equal(fileContent, didOpen["params"]?["textDocument"]?["text"]?.GetValue<string>());
        var hover = JsonSerializer.Deserialize<JsonObject>(server.ForwardedFrames[1])!;
        Assert.Equal("textDocument/hover", hover["method"]?.GetValue<string>());
        Assert.Single(readFileCalls);
        Assert.Equal(filePath, readFileCalls[0]);
    }

    [Fact]
    public async Task TextDocument_RequestWithId_DidOpenAlreadySent_DoesNotSynthesizeAgain()
    {
        var sln = "/repo/App.slnx";
        var filePath = "/repo/src/Foo.cs";
        var readFileCount = 0;
        var (dispatcher, _, _, pool) = Make(
            routeResult: sln,
            readFile: _ => { readFileCount++; return Task.FromResult("class Foo {}"); });
        var server = (FakeServer)await pool.GetOrAddAsync(sln);

        var didOpenMsg = Msg("textDocument/didOpen",
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri(filePath) } });
        await dispatcher.HandleMessageAsync(didOpenMsg);
        server.ForwardedFrames.Clear();

        var hoverMsg = Msg("textDocument/hover", id: JsonValue.Create(2),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri(filePath) } });
        await dispatcher.HandleMessageAsync(hoverMsg);

        Assert.Single(server.ForwardedFrames);
        var hover = JsonSerializer.Deserialize<JsonObject>(server.ForwardedFrames[0])!;
        Assert.Equal("textDocument/hover", hover["method"]?.GetValue<string>());
        Assert.Equal(0, readFileCount);
    }

    [Fact]
    public async Task TextDocument_DidOpen_MarksUriOpened_NoSynthesis()
    {
        var sln = "/repo/App.slnx";
        var filePath = "/repo/src/Foo.cs";
        var readFileCount = 0;
        var (dispatcher, _, _, pool) = Make(
            routeResult: sln,
            readFile: _ => { readFileCount++; return Task.FromResult(""); });
        var server = (FakeServer)await pool.GetOrAddAsync(sln);

        var didOpenMsg = Msg("textDocument/didOpen",
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri(filePath) } });
        await dispatcher.HandleMessageAsync(didOpenMsg);

        Assert.Single(server.ForwardedFrames);
        var forwarded = JsonSerializer.Deserialize<JsonObject>(server.ForwardedFrames[0])!;
        Assert.Equal("textDocument/didOpen", forwarded["method"]?.GetValue<string>());
        Assert.Equal(0, readFileCount);
    }

    [Fact]
    public async Task TextDocument_DidClose_ThenRequest_SynthesizesDidOpenAgain()
    {
        var sln = "/repo/App.slnx";
        var filePath = "/repo/src/Foo.cs";
        var readFileCount = 0;
        var (dispatcher, _, _, pool) = Make(
            routeResult: sln,
            readFile: _ => { readFileCount++; return Task.FromResult("class Foo {}"); });
        var server = (FakeServer)await pool.GetOrAddAsync(sln);

        var uri = FileUri(filePath);
        await dispatcher.HandleMessageAsync(Msg("textDocument/didOpen",
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }));
        await dispatcher.HandleMessageAsync(Msg("textDocument/didClose",
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }));
        server.ForwardedFrames.Clear();

        await dispatcher.HandleMessageAsync(Msg("textDocument/hover", id: JsonValue.Create(3),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }));

        Assert.Equal(2, server.ForwardedFrames.Count);
        var synthesized = JsonSerializer.Deserialize<JsonObject>(server.ForwardedFrames[0])!;
        Assert.Equal("textDocument/didOpen", synthesized["method"]?.GetValue<string>());
        Assert.Equal(1, readFileCount);
    }

    [Fact]
    public async Task NotifyEviction_ClearsOpenedUris_SynthesizesOnNextRequest()
    {
        var sln = "/repo/App.slnx";
        var filePath = "/repo/src/Foo.cs";
        var readFileCount = 0;
        var transport = new FakeTransport();
        var router = new FakeRouter { RouteResult = sln };
        var server = new FakeServer();
        var pool = new ServerPool<IChildServer>(10, _ => Task.FromResult<IChildServer>(server));
        var dispatcher = new MuxDispatcher(router, pool, transport,
            _ => { readFileCount++; return Task.FromResult("class Foo {}"); });
        pool.OnEvict = dispatcher.NotifyEviction;
        await pool.GetOrAddAsync(sln);

        var uri = FileUri(filePath);
        await dispatcher.HandleMessageAsync(Msg("textDocument/didOpen",
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }));
        server.ForwardedFrames.Clear();

        dispatcher.NotifyEviction(server);

        await dispatcher.HandleMessageAsync(Msg("textDocument/hover", id: JsonValue.Create(4),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }));

        Assert.Equal(2, server.ForwardedFrames.Count);
        var synthesized = JsonSerializer.Deserialize<JsonObject>(server.ForwardedFrames[0])!;
        Assert.Equal("textDocument/didOpen", synthesized["method"]?.GetValue<string>());
        Assert.Equal(1, readFileCount);
    }

    [Fact]
    public async Task NotifyEviction_RemovesEvictedServerEntries()
    {
        var sln = "/repo/App.slnx";
        var transport = new FakeTransport();
        var router = new FakeRouter { RouteResult = sln };
        var server = new FakeServer();
        var pool = new ServerPool<IChildServer>(10, _ => Task.FromResult<IChildServer>(server));
        var dispatcher = new MuxDispatcher(router, pool, transport, _ => Task.FromResult(""));
        pool.OnEvict = dispatcher.NotifyEviction;

        // Forward a textDocument request to register in requestOwners
        var textDocMsg = Msg("textDocument/hover", id: JsonValue.Create(9),
            @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = FileUri("/repo/src/Foo.cs") } });
        await dispatcher.HandleMessageAsync(textDocMsg);

        // Simulate eviction
        dispatcher.NotifyEviction(server);

        // Cancel should no longer forward (owner was evicted)
        server.ForwardedFrames.Clear();
        var cancelMsg = Msg("$/cancelRequest",
            @params: new JsonObject { ["id"] = JsonValue.Create(9) });
        await dispatcher.HandleMessageAsync(cancelMsg);

        Assert.Empty(server.ForwardedFrames);
    }
}
