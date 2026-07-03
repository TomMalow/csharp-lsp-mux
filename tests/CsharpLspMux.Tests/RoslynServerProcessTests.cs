using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class RoslynServerProcessTests
{
    /// <summary>
    /// Controllable IFrameReader: enqueue JsonObjects to be returned sequentially.
    /// Blocks until a frame is enqueued or EOF is signalled.
    /// </summary>
    private sealed class FakeFrameReader : IFrameReader, IDisposable
    {
        private readonly System.Threading.Channels.Channel<JsonObject?> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<JsonObject?>();

        public void Enqueue(JsonObject frame) => _channel.Writer.TryWrite(frame);
        public void Complete() => _channel.Writer.Complete();

        public async Task<JsonObject?> ReadFrameAsync(CancellationToken ct = default)
            => await _channel.Reader.ReadAsync(ct);

        public void Dispose() => Complete();
    }

    private sealed class FakeTransport : IFrameWriter
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            _channel.Writer.TryWrite(frame.ToArray());
            return Task.CompletedTask;
        }

        public async Task<JsonObject> ReadNextAsync(CancellationToken ct = default)
        {
            var bytes = await _channel.Reader.ReadAsync(ct);
            return JsonSerializer.Deserialize<JsonObject>(bytes)!;
        }
    }

    private static byte[] MakeFrame(JsonObject obj)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));

    private static JsonObject MakeNotification(string method) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method
    };

    private static JsonObject MakeRequest(string method, int id) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method
    };

    private static JsonObject MakeResponse(string id) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id
    };

    private static JsonObject MakeInitializeResponse() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 0,
        ["result"] = new JsonObject { ["capabilities"] = new JsonObject() }
    };

    private static (RoslynServerProcess Server, MemoryStream Stdin) MakeServerWithStdin(FakeFrameReader reader, FakeTransport transport, Func<Task>? onDispose = null, MuxLogger? logger = null, string? solutionPath = null, string? solutionDir = null)
    {
        var stdin = new MemoryStream();
        return (RoslynServerProcess.CreateForTest(stdin, reader, transport, onDispose, logger, solutionPath, solutionDir), stdin);
    }

    private static RoslynServerProcess MakeServer(FakeFrameReader reader, FakeTransport transport, Func<Task>? onDispose = null)
        => MakeServerWithStdin(reader, transport, onDispose).Server;

    [Fact]
    public async Task ForwardRequest_PreInit_TaskCompletesImmediately()
    {
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport);

        var frame = MakeFrame(MakeNotification("textDocument/didOpen"));
        var forwardTask = server.ForwardRequestAsync(frame);

        Assert.True(forwardTask.IsCompleted, "pre-init forward must return immediately (frame queued, not blocking)");
        Assert.Equal(0, stdin.Length);

        reader.Complete();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ForwardRequest_PreInit_FlushesAfterInitialized()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport);
        await using var _ = server;

        var frame = MakeFrame(MakeNotification("textDocument/didOpen"));
        await server.ForwardRequestAsync(frame);

        Assert.Equal(0, stdin.Length);

        reader.Enqueue(MakeInitializeResponse());
        // Wait for init flush: read loop must process the initialize response and flush pending
        await Task.Delay(100, ct);

        Assert.True(stdin.Length > 0, "frame must be written to stdin after init");

        reader.Complete();
    }

    [Fact]
    public async Task InitializeResponse_SendsInitializedNotificationToServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var stdin = new MemoryStream();
        await using var server = RoslynServerProcess.CreateForTest(stdin, reader, transport);

        reader.Enqueue(MakeInitializeResponse());
        // Wait for read loop to process initialize response and send "initialized"
        await Task.Delay(100, ct);

        var written = Encoding.UTF8.GetString(stdin.ToArray());
        Assert.Contains("\"method\":\"initialized\"", written);

        reader.Complete();
    }

    [Fact]
    public async Task SendAndReceiveAsync_RewritesId_ResolvesOnMatchingResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        await using var server = MakeServer(reader, transport);

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(20, ct);

        var request = MakeFrame(MakeRequest("workspace/symbol", 42));
        var receiveTask = server.SendAndReceiveAsync(request);

        await Task.Delay(20, ct);
        Assert.False(receiveTask.IsCompleted);

        reader.Enqueue(MakeResponse("__mux_1"));

        var result = await receiveTask.WaitAsync(ct);
        var parsed = JsonSerializer.Deserialize<JsonObject>(result)!;
        Assert.Equal("__mux_1", parsed["id"]!.GetValue<string>());

        reader.Complete();
    }

    [Fact]
    public async Task ConcurrentSendAndReceiveAsync_BothResolve()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        await using var server = MakeServer(reader, transport);

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(20, ct);

        var t1 = server.SendAndReceiveAsync(MakeFrame(MakeRequest("workspace/symbol", 1)));
        var t2 = server.SendAndReceiveAsync(MakeFrame(MakeRequest("workspace/symbol", 2)));

        await Task.Delay(20, ct);

        reader.Enqueue(MakeResponse("__mux_1"));
        reader.Enqueue(MakeResponse("__mux_2"));

        var results = await Task.WhenAll(
            t1.WaitAsync(ct),
            t2.WaitAsync(ct));

        Assert.Equal(2, results.Length);

        reader.Complete();
    }

    [Fact]
    public async Task DisposeAsync_CancelsReadLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var onDisposeCalled = false;
        await using (RoslynServerProcess.CreateForTest(new MemoryStream(), reader, transport, () => { onDisposeCalled = true; return Task.CompletedTask; }))
        {
            // Reader stays open — read loop is blocking
            await Task.Delay(20, ct);
        }
        // DisposeAsync should complete (read loop cancelled)
        Assert.True(onDisposeCalled);
    }

    [Fact]
    public async Task ReadLoop_RelaysNonPendingResponsesToClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        await using var server = MakeServer(reader, transport);

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(20, ct);

        // Push a notification (no id match in pending) — should relay to client
        reader.Enqueue(MakeNotification("textDocument/publishDiagnostics"));

        var relayed = await transport.ReadNextAsync(ct);
        Assert.Equal("textDocument/publishDiagnostics", relayed["method"]!.GetValue<string>());

        reader.Complete();
    }

    [Fact]
    public async Task ForwardRequest_MultiplePreInit_AllFlushedInOrderAfterInit()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport);
        await using var _ = server;

        var frame1 = MakeFrame(MakeNotification("textDocument/didOpen"));
        var frame2 = MakeFrame(MakeNotification("textDocument/didChange"));
        await server.ForwardRequestAsync(frame1);
        await server.ForwardRequestAsync(frame2);

        Assert.Equal(0, stdin.Length);

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(100, ct);

        var written = Encoding.UTF8.GetString(stdin.ToArray());
        var idx1 = written.IndexOf("textDocument/didOpen", StringComparison.Ordinal);
        var idx2 = written.IndexOf("textDocument/didChange", StringComparison.Ordinal);
        Assert.True(idx1 >= 0, "didOpen must be flushed");
        Assert.True(idx2 >= 0, "didChange must be flushed");
        Assert.True(idx1 < idx2, "frames must flush in enqueue order");

        reader.Complete();
    }

    [Fact]
    public async Task ForwardRequest_AfterInit_WritesImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(50, ct);
        Assert.True(server.IsInitialized, "server must be initialized after response");

        var lengthBeforeForward = stdin.Length;
        var frame = MakeFrame(MakeNotification("textDocument/hover"));
        await server.ForwardRequestAsync(frame);

        Assert.True(stdin.Length > lengthBeforeForward, "post-init forward must write immediately");

        reader.Complete();
    }

    [Fact]
    public async Task ForwardRequest_ConcurrentSignalAndEnqueue_NoFrameLost()
    {
        var ct = TestContext.Current.CancellationToken;
        const int frameCount = 50;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var reader = new FakeFrameReader();
            var transport = new FakeTransport();
            var (server, stdin) = MakeServerWithStdin(reader, transport);
            await using var _ = server;

            // Fire init response concurrently while flooding ForwardRequestAsync
            var forwardTasks = Enumerable.Range(0, frameCount)
                .Select(i => server.ForwardRequestAsync(MakeFrame(MakeNotification($"textDocument/didOpen_{i}"))))
                .ToArray();

            reader.Enqueue(MakeInitializeResponse());
            await Task.WhenAll(forwardTasks);
            await Task.Delay(100, ct);

            var written = Encoding.UTF8.GetString(stdin.ToArray());
            for (var i = 0; i < frameCount; i++)
                Assert.Contains($"didOpen_{i}", written, StringComparison.Ordinal);

            reader.Complete();
        }
    }

    [Fact]
    public async Task SendInitialize_IncludesWorkspaceSymbolCapability()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, solutionPath: "/repo/App.slnx", solutionDir: "/repo");
        await using var _ = server;

        await Task.Delay(50, ct);

        var raw = Encoding.UTF8.GetString(stdin.ToArray());
        var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(headerEnd >= 0, "No LSP frame written to server stdin");
        var initRequest = JsonSerializer.Deserialize<JsonObject>(raw[(headerEnd + 4)..])!;
        Assert.NotNull(initRequest["params"]?["capabilities"]?["workspace"]?["symbol"]);

        reader.Complete();
    }

    [Fact]
    public async Task WorkspaceConfiguration_Request_InterceptedAndAnswered()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(50, ct);
        Assert.True(server.IsInitialized);

        var stdinLengthAfterInit = stdin.Length;

        var configRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 99,
            ["method"] = "workspace/configuration",
            ["params"] = new JsonObject { ["items"] = new JsonArray() }
        };
        reader.Enqueue(configRequest);
        await Task.Delay(100, ct);

        // Response must be written back to the child server (stdin grew), not relayed to the client
        Assert.True(stdin.Length > stdinLengthAfterInit, "response must be written to child server stdin");

        var stdinContent = Encoding.UTF8.GetString(stdin.ToArray());
        Assert.Contains("\"id\":99", stdinContent);
        Assert.Contains("\"result\":[{}]", stdinContent);

        reader.Complete();
    }

    [Fact]
    public async Task InitializeResponse_LogsInitializedWithElapsedTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(enabled: true, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger, solutionPath: "/repo/App.slnx");
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(100, ct);

        var output = logWriter.ToString();
        Assert.Contains("[mux] server /repo/App.slnx initialized in", output);
        Assert.Contains("ms", output);

        reader.Complete();
    }
}
