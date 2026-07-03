using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class MuxDispatcherLoggingTests
{
    private sealed class FakeTransport : IFrameWriter
    {
        public Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRouter : ISolutionRouter
    {
        public string? RouteResult { get; set; }
        public string? Route(string absoluteFilePath) => RouteResult;
        public void NotifyFileChanged(string changedPath) { }
    }

    private sealed class FakeServer : IChildServer
    {
        public bool IsInitialized => true;
        public event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(byte[] frame) => Task.CompletedTask;
        public Task<byte[]> SendAndReceiveAsync(byte[] frame) => Task.FromResult(Array.Empty<byte>());
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeServerPool : IServerPool<IChildServer>
    {
        private readonly IChildServer _server;
        public FakeServerPool(IChildServer server) => _server = server;
        public event Action<IChildServer>? Evicted { add { } remove { } }
        public Task<IChildServer> GetOrAddAsync(string key) => Task.FromResult(_server);
        public IEnumerable<IChildServer> ActiveServers => [_server];
        public Task DisposeAllAsync() => Task.CompletedTask;
    }

    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    private static JsonObject TextDocMsg(string method, string filePath, int? id = null)
    {
        var o = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = FileUri(filePath) }
            }
        };
        if (id.HasValue) o["id"] = id.Value;
        return o;
    }

    [Fact]
    public async Task LoggerDisabled_NoOutput_WhenNoSolutionFound()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(enabled: false, writer);
        var router = new FakeRouter { RouteResult = null };
        var pool = new FakeServerPool(new FakeServer());
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public async Task LoggerEnabled_LogsNoSolutionFound_WhenRouterReturnsNull()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(enabled: true, writer);
        var router = new FakeRouter { RouteResult = null };
        var pool = new FakeServerPool(new FakeServer());
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        var output = writer.ToString();
        Assert.Contains("[mux] SolutionRouter: no solution found for", output);
        Assert.Contains("Foo.cs", output);
    }

    [Fact]
    public async Task LoggerEnabled_LogsRouteWithInitialized_WhenServerIsInitialized()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(enabled: true, writer);
        var router = new FakeRouter { RouteResult = "/repo/App.slnx" };
        var server = new FakeServer();
        var pool = new FakeServerPool(server);
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(),
            readFile: _ => Task.FromResult(""), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        var output = writer.ToString();
        Assert.Contains("[mux] route textDocument/hover", output);
        Assert.Contains("App.slnx", output);
        Assert.Contains("initialized", output);
    }

    [Fact]
    public async Task LoggerEnabled_LogsRouteWithQueued_WhenServerNotYetInitialized()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(enabled: true, writer);
        var router = new FakeRouter { RouteResult = "/repo/App.slnx" };
        var server = new NotInitializedFakeServer();
        var pool = new FakeServerPool(server);
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(),
            readFile: _ => Task.FromResult(""), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        var output = writer.ToString();
        Assert.Contains("[mux] route textDocument/hover", output);
        Assert.Contains("starting, queued", output);
    }

    private sealed class NotInitializedFakeServer : IChildServer
    {
        public bool IsInitialized => false;
        public event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(byte[] frame) => Task.CompletedTask;
        public Task<byte[]> SendAndReceiveAsync(byte[] frame) => Task.FromResult(Array.Empty<byte>());
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
