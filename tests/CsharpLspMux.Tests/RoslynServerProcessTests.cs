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
        private readonly System.Threading.Channels.Channel<Frame?> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<Frame?>();

        public void Enqueue(JsonObject frame) => _channel.Writer.TryWrite(Frame.FromJson(frame));
        public void Complete() => _channel.Writer.Complete();

        public async Task<Frame?> ReadFrameAsync(CancellationToken ct = default)
            => await _channel.Reader.ReadAsync(ct);

        public void Dispose() => Complete();
    }

    private sealed class FakeTransport : IFrameWriter
    {
        private readonly System.Threading.Channels.Channel<byte[]> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public Task WriteFrameAsync(Frame frame, CancellationToken ct = default)
        {
            _channel.Writer.TryWrite(frame.Wire.ToArray());
            return Task.CompletedTask;
        }

        public async Task<JsonObject> ReadNextAsync(CancellationToken ct = default)
        {
            var bytes = await _channel.Reader.ReadAsync(ct);
            return JsonSerializer.Deserialize<JsonObject>(bytes)!;
        }

        public bool TryReadNext(out JsonObject? frame)
        {
            if (_channel.Reader.TryRead(out var bytes))
            {
                frame = JsonSerializer.Deserialize<JsonObject>(bytes)!;
                return true;
            }
            frame = null;
            return false;
        }
    }

    private static Frame MakeFrame(JsonObject obj) => Frame.FromJson(obj);

    /// <summary>
    /// Parses all sequential Content-Length-framed JSON messages out of a raw byte buffer,
    /// in the order they were written.
    /// </summary>
    private static List<JsonObject> ParseFrames(byte[] raw)
    {
        var frames = new List<JsonObject>();
        var text = Encoding.UTF8.GetString(raw);
        var offset = 0;
        while (offset < text.Length)
        {
            var headerEnd = text.IndexOf("\r\n\r\n", offset, StringComparison.Ordinal);
            if (headerEnd < 0) break;
            var header = text[offset..headerEnd];
            var lengthLine = header.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = int.Parse(lengthLine.Split(':')[1].Trim());
            var bodyStart = headerEnd + 4;
            var body = text.Substring(bodyStart, length);
            frames.Add(JsonSerializer.Deserialize<JsonObject>(body)!);
            offset = bodyStart + length;
        }
        return frames;
    }

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

    private static (RoslynServerProcess Server, MemoryStream Stdin) MakeServerWithStdin(
        FakeFrameReader reader, FakeTransport transport,
        Func<Task>? onDispose = null, MuxLogger? logger = null,
        string? solutionPath = null, string? solutionDir = null,
        int hardTimeoutMs = 50)
    {
        var stdin = new MemoryStream();
        var server = RoslynServerProcess.CreateForTest(stdin, reader, onDispose, logger, solutionPath, solutionDir, hardTimeoutMs);
        server.OnRelayFrame += frame => new ValueTask(transport.WriteFrameAsync(frame));
        return (server, stdin);
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
        await using var server = RoslynServerProcess.CreateForTest(stdin, reader);

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
        Assert.Equal("__mux_1", result.Id!.GetValue<string>());

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
        await using (RoslynServerProcess.CreateForTest(new MemoryStream(), reader, () => { onDisposeCalled = true; return Task.CompletedTask; }))
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
    public async Task ReadLoop_RelayFrameArrivesViaOnRelayFrameEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var relayedFrames = new List<byte[]>();
        var stdin = new MemoryStream();
        await using var server = RoslynServerProcess.CreateForTest(stdin, reader);
        server.OnRelayFrame += frame => { relayedFrames.Add(frame.Wire.ToArray()); return ValueTask.CompletedTask; };

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(20, ct);

        reader.Enqueue(MakeNotification("textDocument/publishDiagnostics"));
        await Task.Delay(50, ct);

        Assert.Single(relayedFrames);
        var parsed = JsonSerializer.Deserialize<JsonObject>(relayedFrames[0])!;
        Assert.Equal("textDocument/publishDiagnostics", parsed["method"]!.GetValue<string>());

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
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 50);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(100, ct);
        Assert.Equal(ServerReadiness.Ready, server.Readiness);

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
    public async Task SendInitialize_IncludesWindowWorkDoneProgressCapability()
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
        Assert.True(initRequest["params"]?["capabilities"]?["window"]?["workDoneProgress"]?.GetValue<bool>());

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
        await Task.Delay(100, ct);
        Assert.Equal(ServerReadiness.Ready, server.Readiness);

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

        // Response must be written back to the child server (stdin grew), not relayed to the client.
        // Response content (id echo, empty-settings result) is InboundClassifier's contract —
        // covered by InboundClassifierTests.WorkspaceConfiguration_IsRespondToChild_WithEmptySettingsArrayAndEchoedId.
        Assert.True(stdin.Length > stdinLengthAfterInit, "response must be written to child server stdin");
        Assert.False(transport.TryReadNext(out JsonObject? unused), "workspace/configuration must not be forwarded to client");

        reader.Complete();
    }

    [Fact]
    public async Task InitializeResponse_LogsInitializedWithElapsedTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger, solutionPath: "/repo/App.slnx");
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(100, ct);

        var output = logWriter.ToString();
        Assert.Contains("[mux] server /repo/App.slnx initialized in", output);
        Assert.Contains("ms", output);

        reader.Complete();
    }

    [Fact]
    public async Task Readiness_InitialState_IsStarting()
    {
        var reader = new FakeFrameReader();
        await using var server = RoslynServerProcess.CreateForTest(new MemoryStream(), reader);

        Assert.Equal(ServerReadiness.Starting, server.Readiness);

        reader.Complete();
    }

    [Fact]
    public async Task ForwardNotification_PreInit_TaskCompletesImmediately()
    {
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport);

        var frame = MakeFrame(MakeNotification("textDocument/didOpen"));
        var forwardTask = server.ForwardNotificationAsync(frame);

        Assert.True(forwardTask.IsCompleted, "pre-init notification forward must return immediately (frame queued, not blocking)");
        Assert.Equal(0, stdin.Length);

        reader.Complete();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ForwardNotification_PreInit_FlushesAfterInitialized()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport);
        await using var _ = server;

        var frame = MakeFrame(MakeNotification("textDocument/didOpen"));
        await server.ForwardNotificationAsync(frame);

        Assert.Equal(0, stdin.Length);

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(100, ct);

        Assert.True(stdin.Length > 0, "notification frame must be written to stdin after init");

        reader.Complete();
    }

    [Fact]
    public async Task ForwardNotification_AfterInit_WritesImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 50);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(100, ct);
        Assert.Equal(ServerReadiness.Ready, server.Readiness);

        var lengthBefore = stdin.Length;
        var frame = MakeFrame(MakeNotification("textDocument/didOpen"));
        await server.ForwardNotificationAsync(frame);

        Assert.True(stdin.Length > lengthBefore, "post-init notification forward must write immediately");

        reader.Complete();
    }

    [Fact]
    public async Task ForwardRequest_DuringInitialized_QueuesUntilHardTimeoutFires()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 100);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct); // past init processing, before hard timeout

        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        var lengthAfterInit = stdin.Length;
        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);
        Assert.Equal(lengthAfterInit, stdin.Length); // still queued

        await Task.Delay(150, ct); // past hard timeout (100ms)

        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        Assert.True(stdin.Length > lengthAfterInit, "request must be flushed after hard timeout fires");

        reader.Complete();
    }

    [Fact]
    public async Task ForwardNotification_DuringInitialized_WritesImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 10000);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);

        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        var lengthBefore = stdin.Length;
        var frame = MakeFrame(MakeNotification("textDocument/didOpen"));
        await server.ForwardNotificationAsync(frame);

        Assert.True(stdin.Length > lengthBefore, "notification must bypass Ready gate and write immediately when Initialized");

        reader.Complete();
    }

    [Fact]
    public async Task HardTimeout_FlushesWithWarningLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, stdin) = MakeServerWithStdin(reader, transport, logger: logger, hardTimeoutMs: 60);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);
        var lengthBeforeTimeout = stdin.Length;

        await Task.Delay(150, ct); // past hard timeout (60ms)

        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        Assert.True(stdin.Length > lengthBeforeTimeout, "request must be flushed after hard timeout");

        var log = logWriter.ToString();
        Assert.Contains("[mux] workspace load timeout", log);
        Assert.Contains("queued requests", log);

        reader.Complete();
    }

    [Fact]
    public async Task WorkspaceReadyLog_EmittedOnTransitionToReady()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger, solutionPath: "/repo/App.slnx", hardTimeoutMs: 60);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(150, ct);

        var log = logWriter.ToString();
        Assert.Contains("[mux] server /repo/App.slnx workspace ready in", log);
        Assert.Contains("ms", log);

        reader.Complete();
    }

    // --- Progress tracking helpers ---

    private static JsonObject MakeWorkDoneProgressCreate(int id, string token) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "window/workDoneProgress/create",
        ["params"] = new JsonObject { ["token"] = token }
    };

    private static JsonObject MakeProgressBegin(string token, string title) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "$/progress",
        ["params"] = new JsonObject
        {
            ["token"] = token,
            ["value"] = new JsonObject { ["kind"] = "begin", ["title"] = title }
        }
    };

    private static JsonObject MakeProgressEnd(string token) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "$/progress",
        ["params"] = new JsonObject
        {
            ["token"] = token,
            ["value"] = new JsonObject { ["kind"] = "end" }
        }
    };

    [Fact]
    public async Task WorkDoneProgressCreate_InterceptedAndAutoResponded()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        var stdinLengthBefore = stdin.Length;
        reader.Enqueue(MakeWorkDoneProgressCreate(10, "token1"));
        await Task.Delay(50, ct);

        // Auto-response written to child server stdin. Response content (id echo, null result)
        // is InboundClassifier's contract — covered by
        // InboundClassifierTests.WorkDoneProgressCreate_IsRespondToChild_WithNullResultAndEchoedId.
        Assert.True(stdin.Length > stdinLengthBefore, "auto-response must be written to child server stdin");

        // Must NOT be relayed to client transport
        Assert.False(transport.TryReadNext(out JsonObject? unused), "window/workDoneProgress/create must not be forwarded to client");

        reader.Complete();
    }

    [Fact]
    public async Task Progress_HappyPath_BeginThenEnd_FlushesRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        reader.Enqueue(MakeWorkDoneProgressCreate(10, "token1"));
        reader.Enqueue(MakeProgressBegin("token1", "Loading workspace..."));
        await Task.Delay(30, ct);

        // Request queued while still Initialized, progress active
        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);
        var stdinBeforeEnd = stdin.Length;

        reader.Enqueue(MakeProgressEnd("token1"));
        await Task.Delay(50, ct);

        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        Assert.True(stdin.Length > stdinBeforeEnd, "queued request must flush after progress end");

        reader.Complete();
    }

    [Fact]
    public async Task Progress_MultipleTokens_GatedUntilLastEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);

        reader.Enqueue(MakeWorkDoneProgressCreate(10, "tokenA"));
        reader.Enqueue(MakeWorkDoneProgressCreate(11, "tokenB"));
        reader.Enqueue(MakeProgressBegin("tokenA", "Loading solution..."));
        reader.Enqueue(MakeProgressBegin("tokenB", "Loading projects..."));
        await Task.Delay(30, ct);

        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);
        var stdinBeforeFirstEnd = stdin.Length;

        // First end — still one token active, gate must hold
        reader.Enqueue(MakeProgressEnd("tokenA"));
        await Task.Delay(50, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);
        Assert.Equal(stdinBeforeFirstEnd, stdin.Length);

        // Second end — all tokens done, gate opens
        reader.Enqueue(MakeProgressEnd("tokenB"));
        await Task.Delay(50, ct);
        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        Assert.True(stdin.Length > stdinBeforeFirstEnd, "queued request must flush after last progress end");

        reader.Complete();
    }

    [Fact]
    public async Task Progress_NonLoadingTitle_DoesNotBlockHardTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 80);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(20, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        // Non-loading progress does not set _seenLoadingToken, so hard timeout still fires
        reader.Enqueue(MakeProgressBegin("tokenX", "Analyzing code"));
        await Task.Delay(150, ct); // past hard timeout (80ms)

        Assert.Equal(ServerReadiness.Ready, server.Readiness);

        reader.Complete();
    }

    [Fact]
    public async Task HardTimeout_WithLoadingProgress_StillFlushes()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, stdin) = MakeServerWithStdin(reader, transport, logger: logger, hardTimeoutMs: 80);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);

        // Loading begins — hard timeout becomes the only fallback
        reader.Enqueue(MakeWorkDoneProgressCreate(10, "token1"));
        reader.Enqueue(MakeProgressBegin("token1", "Loading workspace..."));
        await Task.Delay(20, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);
        var stdinBeforeTimeout = stdin.Length;

        // Progress end never arrives — hard timeout must fire
        await Task.Delay(200, ct); // well past hard timeout (80ms)

        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        Assert.True(stdin.Length > stdinBeforeTimeout, "queued request must flush after hard timeout even when loading progress started");
        Assert.Contains("[mux] workspace load timeout", logWriter.ToString());

        reader.Complete();
    }

    [Fact]
    public async Task ForwardRequest_Queued_DebugLogsMethodAndId()
    {
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger);
        await using var _ = server;

        await server.ForwardRequestAsync(MakeFrame(MakeRequest("textDocument/references", 42)));

        var output = logWriter.ToString();
        Assert.Contains("textDocument/references", output);
        Assert.Contains("42", output);

        reader.Complete();
    }

    [Fact]
    public async Task ForwardRequest_Queued_InfoLevel_NoDebugLog()
    {
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Info, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger);
        await using var _ = server;

        await server.ForwardRequestAsync(MakeFrame(MakeRequest("textDocument/references", 42)));

        Assert.Equal("", logWriter.ToString());

        reader.Complete();
    }

    [Fact]
    public async Task ForwardNotification_Queued_DebugLogsMethod()
    {
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger);
        await using var _ = server;

        await server.ForwardNotificationAsync(MakeFrame(MakeNotification("textDocument/didOpen")));

        Assert.Contains("textDocument/didOpen", logWriter.ToString());

        reader.Complete();
    }

    [Fact]
    public async Task TransitionToReady_DebugLogsDrainCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger, hardTimeoutMs: 80);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        await server.ForwardRequestAsync(MakeFrame(MakeRequest("textDocument/references", 1)));
        await server.ForwardRequestAsync(MakeFrame(MakeRequest("textDocument/hover", 2)));

        await Task.Delay(200, ct); // past hard timeout (80ms)
        Assert.Equal(ServerReadiness.Ready, server.Readiness);

        Assert.Contains("draining 2 queued requests", logWriter.ToString());

        reader.Complete();
    }

    [Fact]
    public async Task HardTimeout_DebugLogsHardTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger, hardTimeoutMs: 60);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(200, ct);

        Assert.Contains("workspace ready via hard timeout", logWriter.ToString());

        reader.Complete();
    }

    [Fact]
    public async Task ReadyBeforeHardTimeout_DoesNotLogFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger, hardTimeoutMs: 60);
        await using var _s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(20, ct); // past init, before hard timeout (60ms)
        reader.Enqueue(MakeNotification("workspace/projectInitializationComplete"));

        await Task.Delay(200, ct); // well past hard timeout — fallback timer should have been disarmed

        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        var log = logWriter.ToString();
        Assert.DoesNotContain("fired as fallback", log);
        Assert.DoesNotContain("workspace ready via hard timeout", log);

        reader.Complete();
    }

    [Fact]
    public async Task RelayResponse_DebugLogsId()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Debug, logWriter);

        var (server, _) = MakeServerWithStdin(reader, transport, logger: logger, hardTimeoutMs: 60);
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(100, ct);
        Assert.Equal(ServerReadiness.Ready, server.Readiness);

        await server.ForwardRequestAsync(MakeFrame(MakeRequest("textDocument/references", 99)));
        reader.Enqueue(MakeResponse("99"));
        await Task.Delay(50, ct);

        var output = logWriter.ToString();
        Assert.Contains("relay response id=", output);
        Assert.Contains("99", output);

        reader.Complete();
    }

    [Fact]
    public async Task Progress_SwallowedNotForwardedToClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);

        reader.Enqueue(MakeProgressBegin("tokenP", "Loading workspace..."));
        reader.Enqueue(MakeProgressEnd("tokenP"));
        await Task.Delay(50, ct);

        // Transport must have received no $/progress frames
        while (transport.TryReadNext(out var frame))
            Assert.NotEqual("$/progress", frame?["method"]?.GetValue<string>());

        reader.Complete();
    }

    [Fact]
    public async Task ProjectInitializationComplete_TransitionsToReadyAndDrainsRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);
        var stdinBefore = stdin.Length;

        reader.Enqueue(MakeNotification("workspace/projectInitializationComplete"));
        await Task.Delay(50, ct);

        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        var written = Encoding.UTF8.GetString(stdin.ToArray());
        Assert.Contains("textDocument/references", written, StringComparison.Ordinal);

        reader.Complete();
    }

    [Fact]
    public async Task HardTimeout_WithoutInitializationComplete_StillTransitionsToReady()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 80);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        // workspace/projectInitializationComplete is never sent — hard timeout must still fire
        await Task.Delay(200, ct);

        Assert.Equal(ServerReadiness.Ready, server.Readiness);

        reader.Complete();
    }

    [Fact]
    public async Task HardTimeout_LogMessage_ContainsFiredAsFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Info, logWriter);

        var (server, stdin) = MakeServerWithStdin(reader, transport, logger: logger, hardTimeoutMs: 60);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);

        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);

        await Task.Delay(200, ct); // past hard timeout (60ms)

        Assert.Contains("fired as fallback", logWriter.ToString());

        reader.Complete();
    }

    [Fact]
    public async Task ProjectInitializationComplete_InfoLogEmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var logWriter = new StringWriter();
        var logger = new MuxLogger(LogLevel.Info, logWriter);

        var (server, stdin) = MakeServerWithStdin(reader, transport, logger: logger,
            solutionPath: "/repo/App.slnx", hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);

        reader.Enqueue(MakeNotification("workspace/projectInitializationComplete"));
        await Task.Delay(50, ct);

        Assert.Contains("workspace/projectInitializationComplete", logWriter.ToString());

        reader.Complete();
    }

    [Fact]
    public async Task InitializeResponse_SendsSolutionOpenWithFileUri()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, solutionPath: "/repo/App.slnx", solutionDir: "/repo");
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(50, ct);

        var frames = ParseFrames(stdin.ToArray());
        var solutionOpen = frames.SingleOrDefault(f => f["method"]?.GetValue<string>() == "solution/open");
        Assert.NotNull(solutionOpen);
        Assert.Equal(new Uri("/repo/App.slnx").AbsoluteUri, solutionOpen!["params"]?["solution"]?.GetValue<string>());

        reader.Complete();
    }

    [Fact]
    public async Task InitializeResponse_SolutionOpenSentAfterInitialized()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, solutionPath: "/repo/App.slnx", solutionDir: "/repo");
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(50, ct);

        var frames = ParseFrames(stdin.ToArray());
        var initializedIndex = frames.FindIndex(f => f["method"]?.GetValue<string>() == "initialized");
        var solutionOpenIndex = frames.FindIndex(f => f["method"]?.GetValue<string>() == "solution/open");

        Assert.True(initializedIndex >= 0, "initialized notification must be sent");
        Assert.True(solutionOpenIndex >= 0, "solution/open notification must be sent");
        Assert.True(solutionOpenIndex > initializedIndex, "solution/open must be sent after initialized");

        reader.Complete();
    }

    [Fact]
    public async Task SolutionOpen_NotRelayedToClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, solutionPath: "/repo/App.slnx", solutionDir: "/repo");
        await using var _ = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(50, ct);

        while (transport.TryReadNext(out var frame))
            Assert.NotEqual("solution/open", frame?["method"]?.GetValue<string>());

        reader.Complete();
    }

    [Fact]
    public async Task SolutionOpen_ThenProjectInitializationComplete_ReachesReadyAndDrains()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, solutionPath: "/repo/App.slnx", solutionDir: "/repo", hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(50, ct);
        Assert.Equal(ServerReadiness.Initialized, server.Readiness);

        var frames = ParseFrames(stdin.ToArray());
        Assert.Contains(frames, f => f["method"]?.GetValue<string>() == "solution/open");

        var frame = MakeFrame(MakeRequest("textDocument/references", 1));
        await server.ForwardRequestAsync(frame);

        reader.Enqueue(MakeNotification("workspace/projectInitializationComplete"));
        await Task.Delay(50, ct);

        Assert.Equal(ServerReadiness.Ready, server.Readiness);
        var written = Encoding.UTF8.GetString(stdin.ToArray());
        Assert.Contains("textDocument/references", written, StringComparison.Ordinal);

        reader.Complete();
    }

    [Fact]
    public async Task SendInitialize_DoesNotIncludeSolutionPathInitializationOption()
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
        Assert.Null(initRequest["params"]?["initializationOptions"]?["solutionPath"]);

        reader.Complete();
    }

    [Fact]
    public async Task ProjectInitializationComplete_NotRelayedToClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var reader = new FakeFrameReader();
        var transport = new FakeTransport();
        var (server, stdin) = MakeServerWithStdin(reader, transport, hardTimeoutMs: 10000);
        await using var s = server;

        reader.Enqueue(MakeInitializeResponse());
        await Task.Delay(30, ct);

        reader.Enqueue(MakeNotification("workspace/projectInitializationComplete"));
        await Task.Delay(50, ct);

        Assert.False(transport.TryReadNext(out _), "workspace/projectInitializationComplete must not be relayed to client");

        reader.Complete();
    }
}
