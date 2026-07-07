using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class MuxDispatcherLoggingTests
{
    private sealed class FakeTransport : IFrameWriter
    {
        public Task WriteFrameAsync(Frame frame, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRouter : ISolutionRouter
    {
        public string? RouteResult { get; set; }
        public string? Route(string absoluteFilePath) => RouteResult;
        public void NotifyFileChanged(string changedPath) { }
    }

    private sealed class FakeServer : IChildServer
    {
        public ServerReadiness Readiness => ServerReadiness.Ready;
        public event Func<Frame, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(Frame frame) => Task.CompletedTask;
        public Task ForwardNotificationAsync(Frame frame) => Task.CompletedTask;
        public Task<Frame> SendAndReceiveAsync(Frame frame) => Task.FromResult(Frame.FromJson(new JsonObject()));
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeServerPool : IServerPool<IChildServer>
    {
        private readonly IChildServer _server;
        public FakeServerPool(IChildServer server) => _server = server;
        public Func<IChildServer, Task>? OnEviction { get; set; }
        public Task<IChildServer> GetOrAddAsync(string key) => Task.FromResult(_server);
        public IEnumerable<IChildServer> ActiveServers => [_server];
        public Task DisposeAllAsync() => Task.CompletedTask;
    }

    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    private static Frame TextDocMsg(string method, string filePath, int? id = null)
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
        return Frame.FromJson(o);
    }

    [Fact]
    public async Task LoggerDisabled_NoOutput_WhenNoSolutionFound()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Off, writer);
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
        var logger = new MuxLogger(LogLevel.Debug, writer);
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
        var logger = new MuxLogger(LogLevel.Debug, writer);
        var router = new FakeRouter { RouteResult = "/repo/App.slnx" };
        var server = new FakeServer();
        var pool = new FakeServerPool(server);
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(),
            readFile: _ => Task.FromResult(""), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        var output = writer.ToString();
        Assert.Contains("[mux] route textDocument/hover", output);
        Assert.Contains("App.slnx", output);
        Assert.Contains("ready", output);
    }

    [Fact]
    public async Task LoggerEnabled_LogsRouteWithQueued_WhenServerNotYetInitialized()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, writer);
        var router = new FakeRouter { RouteResult = "/repo/App.slnx" };
        var server = new NotInitializedFakeServer();
        var pool = new FakeServerPool(server);
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(),
            readFile: _ => Task.FromResult(""), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        var output = writer.ToString();
        Assert.Contains("[mux] route textDocument/hover", output);
        Assert.Contains("starting", output);
    }

    [Fact]
    public async Task EnsureOpenAsync_DebugLogsSyntheticDidOpenUri()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, writer);
        var router = new FakeRouter { RouteResult = "/repo/App.slnx" };
        var pool = new FakeServerPool(new FakeServer());
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(),
            readFile: _ => Task.FromResult(""), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        var output = writer.ToString();
        Assert.Contains("synthetic didOpen", output);
        Assert.Contains("Foo.cs", output);
    }

    [Fact]
    public async Task EnsureOpenAsync_InfoLevel_NoDebugLog()
    {
        var writer = new StringWriter();
        var logger = new MuxLogger(LogLevel.Info, writer);
        var router = new FakeRouter { RouteResult = "/repo/App.slnx" };
        var pool = new FakeServerPool(new FakeServer());
        var dispatcher = new MuxDispatcher(router, pool, new FakeTransport(),
            readFile: _ => Task.FromResult(""), logger: logger);

        await dispatcher.HandleMessageAsync(TextDocMsg("textDocument/hover", "/repo/src/Foo.cs", id: 1));

        Assert.DoesNotContain("synthetic didOpen", writer.ToString());
    }

    private sealed class NotInitializedFakeServer : IChildServer
    {
        public ServerReadiness Readiness => ServerReadiness.Starting;
        public event Func<Frame, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(Frame frame) => Task.CompletedTask;
        public Task ForwardNotificationAsync(Frame frame) => Task.CompletedTask;
        public Task<Frame> SendAndReceiveAsync(Frame frame) => Task.FromResult(Frame.FromJson(new JsonObject()));
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
