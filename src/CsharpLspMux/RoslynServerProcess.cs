using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

/// <summary>
/// Wraps a single roslyn-language-server child process.
/// Queues outbound notifications until <c>Initialized</c>, then queues requests until <c>Ready</c>.
/// </summary>
public sealed class RoslynServerProcess : IChildServer
{
    private readonly Stream _stdin;
    private readonly IFrameReader _reader;
    private readonly Func<Task>? _onDispose;
    private readonly MuxLogger? _logger;
    private readonly string? _solutionPath;

    public event Func<Frame, ValueTask>? OnRelayFrame;

    private readonly WorkspaceReadiness _readiness = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly int _hardTimeoutMs;

    private readonly CancellationTokenSource _cts = new();
    // Disarms the hard-timeout fallback the moment a real readiness signal arrives.
    private readonly CancellationTokenSource _readyCts = new();
    private readonly Task _readerTask;
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Frame>> _pending = new();
    private int _syntheticIdCounter;
    private int _disposed;
    // Cleared the moment the id==0 initialize response is observed; guards InboundClassifier's
    // recognition of that response so a later, unrelated id==0 frame isn't misread as it.
    private bool _awaitingInitialize = true;

    public ServerReadiness Readiness => _readiness.State;

    private RoslynServerProcess(Stream stdin, IFrameReader reader, Func<Task>? onDispose, MuxLogger? logger = null, string? solutionPath = null, int hardTimeoutMs = -1)
    {
        _stdin = stdin;
        _reader = reader;
        _onDispose = onDispose;
        _logger = logger;
        _solutionPath = solutionPath;
        _hardTimeoutMs = hardTimeoutMs > 0 ? hardTimeoutMs : ReadHardTimeoutMs();
        _readerTask = Task.Run(ReadLoopAsync);
    }

    private static int ReadHardTimeoutMs()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("LSP_MUX_LOAD_TIMEOUT_MS"), out var ms) && ms > 0)
            return ms;
        return 30000;
    }

    internal static RoslynServerProcess CreateForTest(
        Stream stdin,
        IFrameReader reader,
        Func<Task>? onDispose = null,
        MuxLogger? logger = null,
        string? solutionPath = null,
        string? solutionDir = null,
        int hardTimeoutMs = 30000)
    {
        var server = new RoslynServerProcess(stdin, reader, onDispose, logger, solutionPath, hardTimeoutMs);
        if (solutionPath != null && solutionDir != null)
            server.SendInitialize(solutionPath, solutionDir);
        return server;
    }

    public static RoslynServerProcess Start(string solutionPath, MuxLogger? logger = null)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        var psi = new ProcessStartInfo("roslyn-language-server")
        {
            ArgumentList = { "--stdio" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = solutionDir,
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start roslyn-language-server");
        var server = new RoslynServerProcess(
            process.StandardInput.BaseStream,
            new LspFrameReader(process.StandardOutput.BaseStream),
            async () =>
            {
                if (!process.HasExited)
                {
                    if (!process.WaitForExit(500))
                        process.Kill();
                }
                process.Dispose();
            },
            logger: logger,
            solutionPath: solutionPath);
        _ = server.ReadStderrAsync(process.StandardError); // fire-and-forget: cancelled via _cts in DisposeAsync
        server.SendInitialize(solutionPath, solutionDir);
        return server;
    }

    private void SendInitialize(string solutionPath, string solutionDir)
    {
        var initRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = new Uri(solutionDir).AbsoluteUri,
                ["capabilities"] = Capabilities.BuildRoslynFacingCapabilities()
            }
        };

        _ = WriteFrameAsync(Frame.FromJson(initRequest));
    }

    public Task ForwardRequestAsync(Frame frame)
    {
        if (_readiness.Gate(frame) == GateDecision.Queued)
        {
            if (_logger?.IsDebugEnabled == true)
                _logger.Debug($"queued request {frame.Method ?? "?"} id={frame.Id?.ToJsonString() ?? "?"}");
            return Task.CompletedTask;
        }
        return WriteFrameAsync(frame);
    }

    public Task ForwardNotificationAsync(Frame frame)
    {
        if (_readiness.Gate(frame) == GateDecision.Queued)
        {
            if (_logger?.IsDebugEnabled == true)
                _logger.Debug($"queued notification {frame.Method ?? "?"}");
            return Task.CompletedTask;
        }
        return WriteFrameAsync(frame);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var message = await _reader.ReadFrameAsync(_cts.Token);
                if (message is null) break;

                var ctx = new InboundContext(_pending.Keys.ToHashSet(), _awaitingInitialize);
                await ExecuteAsync(message, InboundClassifier.Classify(message, ctx));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoslynServerProcess] reader error: {ex.Message}");
        }
    }

    /// <summary>Performs the I/O an <see cref="InboundAction"/> calls for; <paramref name="message"/> supplies the frame the action was computed from.</summary>
    private async Task ExecuteAsync(Frame message, InboundAction action)
    {
        switch (action)
        {
            case InboundAction.Signal { Value: ReadinessSignal.Initialized }:
                _awaitingInitialize = false;
                await HandleInitializedAsync();
                break;

            case InboundAction.Signal { Value: ReadinessSignal.ProjectInitializationComplete } signal:
                _logger?.Info($"server {_solutionPath} received workspace/projectInitializationComplete");
                await ApplyBecameReady(_readiness.Observe(signal.Value));
                break;

            case InboundAction.Signal { Value: ReadinessSignal.LoadingBegan } signal:
                _readiness.Observe(signal.Value);
                break;

            case InboundAction.Signal { Value: ReadinessSignal.ProgressEnded } signal:
                await ApplyBecameReady(_readiness.Observe(signal.Value));
                break;

            case InboundAction.RespondToChild respond:
                await WriteFrameAsync(respond.Response);
                break;

            case InboundAction.ResolveCorrelation:
                if (_pending.TryGetValue(message.Id!.ToJsonString(), out var tcs))
                    tcs.TrySetResult(message);
                break;

            case InboundAction.RelayToClient:
                await RelayAsync(message);
                break;

            case InboundAction.Drop:
                break;
        }
    }

    private async Task RelayAsync(Frame message)
    {
        if (OnRelayFrame is { } relay)
        {
            if (_logger?.IsDebugEnabled == true && message.Method is null && message.Id is JsonNode relayId)
                _logger.Debug($"relay response id={relayId.ToJsonString()}");
            await relay(message);
        }
        else
            _logger?.Info($"server {_solutionPath}: no relay subscriber, frame dropped");
    }

    private async Task HandleInitializedAsync()
    {
        var initialized = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JsonObject() };
        await WriteFrameAsync(Frame.FromJson(initialized));
        var initResult = _readiness.Observe(new ReadinessSignal.Initialized());
        if (_logger?.IsInfoEnabled == true)
        {
            var elapsed = (long)(System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
            _logger.Info($"server {_solutionPath} initialized in {elapsed}ms");
        }
        _logger?.Debug($"draining {initResult.FramesToDrain.Count} queued notifications");
        foreach (var f in initResult.FramesToDrain)
            await WriteFrameAsync(f);

        if (_solutionPath is not null)
        {
            var solutionOpen = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "solution/open",
                ["params"] = new JsonObject
                {
                    ["solution"] = new Uri(_solutionPath).AbsoluteUri
                }
            };
            await WriteFrameAsync(Frame.FromJson(solutionOpen));
        }

        // Hard timeout: safety net for servers that never emit a readiness signal.
        // _readyCts disarms it the moment the real signal arrives, so a healthy
        // server neither logs nor runs this — reaching past the delay genuinely
        // means the workspace never finished loading.
        _ = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _readyCts.Token);
                await Task.Delay(_hardTimeoutMs, linked.Token);
                var result = _readiness.Observe(new ReadinessSignal.HardTimeoutElapsed());
                if (result.Transition == ReadinessTransition.BecameReady)
                {
                    _logger?.Info($"workspace load timeout ({_hardTimeoutMs}ms) fired as fallback, forwarding {result.FramesToDrain.Count} queued requests");
                    _logger?.Debug("workspace ready via hard timeout");
                }
                await ApplyBecameReady(result);
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task ApplyBecameReady(ObserveResult result)
    {
        if (result.Transition != ReadinessTransition.BecameReady) return;
        // Disarm the hard-timeout fallback so it stops idling and never logs spuriously.
        try { _readyCts.Cancel(); } catch (ObjectDisposedException) { }
        if (_logger?.IsInfoEnabled == true)
        {
            var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
            _logger.Info($"server {_solutionPath} workspace ready in {elapsed}ms");
        }
        _logger?.Debug($"draining {result.FramesToDrain.Count} queued requests");
        foreach (var f in result.FramesToDrain)
            await WriteFrameAsync(f);
    }

    private async Task WriteFrameAsync(Frame frame)
    {
        var wire = frame.Wire;
        await _writeLock.WaitAsync();
        try
        {
            var header = Encoding.UTF8.GetBytes($"Content-Length: {wire.Length}\r\n\r\n");
            await _stdin.WriteAsync(header);
            await _stdin.WriteAsync(wire);
            await _stdin.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal async Task ReadStderrAsync(System.IO.TextReader stderr)
    {
        try
        {
            string? line;
            while (!_cts.Token.IsCancellationRequested
                   && (line = await stderr.ReadLineAsync(_cts.Token)) is not null)
            {
                _logger?.Info($"server {_solutionPath} stderr: {line}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.Info($"server {_solutionPath} stderr reader error: {ex.Message}");
        }
    }

    public async Task<Frame> SendAndReceiveAsync(Frame frame)
    {
        _cts.Token.ThrowIfCancellationRequested();

        // Rewrite the request id to a synthetic one so we can correlate the response.
        var syntheticId = $"__mux_{Interlocked.Increment(ref _syntheticIdCounter)}";
        var rewritten = frame.WithId(syntheticId);

        // ToJsonString() preserves the JSON-quoted form that ReadLoopAsync extracts from responses.
        var pendingKey = rewritten.Id!.ToJsonString();

        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[pendingKey] = tcs;

        // Cancel the TCS if the server is disposed before a response arrives.
        using var reg = _cts.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            await ForwardRequestAsync(rewritten);
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(pendingKey, out _);
        }
    }

    public async Task ShutdownAsync()
    {
        var shutdown = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = -1,
            ["method"] = "shutdown",
            ["params"] = JsonValue.Create<object?>(null)
        };
        await WriteFrameAsync(Frame.FromJson(shutdown));

        var exit = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "exit"
        };
        await WriteFrameAsync(Frame.FromJson(exit));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync();

        try { await _readerTask; } catch { }

        if (_onDispose is not null)
            await _onDispose();

        _cts.Dispose();
        _readyCts.Dispose();
        _writeLock.Dispose();
    }

}
