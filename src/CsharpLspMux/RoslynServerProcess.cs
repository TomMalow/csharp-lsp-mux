using System.Collections.Concurrent;
using System.Diagnostics;
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

    public event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame;

    private readonly object _initLock = new();
    private ServerReadiness _readiness = ServerReadiness.Starting;
    private readonly List<byte[]> _pendingNotifications = new();
    private readonly List<byte[]> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly int _hardTimeoutMs;
    private readonly HashSet<string> _loadingTokens = new();
    private bool _seenLoadingToken;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerTask;
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pending = new();
    private int _syntheticIdCounter;
    private int _disposed;

    public ServerReadiness Readiness { get { lock (_initLock) return _readiness; } }

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
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["symbol"] = new JsonObject { ["dynamicRegistration"] = false }
                    },
                    ["textDocument"] = new JsonObject
                    {
                        ["synchronization"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["hover"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["definition"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["references"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["documentSymbol"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["completion"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["signatureHelp"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["rename"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["codeAction"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false }
                    }
                },
                ["initializationOptions"] = new JsonObject
                {
                    ["solutionPath"] = solutionPath
                }
            }
        };

        _ = WriteFrameAsync(SerializeFrame(initRequest));
    }

    public Task ForwardRequestAsync(byte[] frame)
    {
        lock (_initLock)
        {
            if (_readiness != ServerReadiness.Ready)
            {
                if (_logger?.IsDebugEnabled == true)
                {
                    var (method, id) = ParseFrameLogFields(frame);
                    _logger.Debug($"queued request {method} id={id}");
                }
                _pendingRequests.Add(frame);
                return Task.CompletedTask;
            }
        }
        return WriteFrameAsync(frame);
    }

    public Task ForwardNotificationAsync(byte[] frame)
    {
        lock (_initLock)
        {
            if (_readiness == ServerReadiness.Starting)
            {
                if (_logger?.IsDebugEnabled == true)
                {
                    var (method, _) = ParseFrameLogFields(frame);
                    _logger.Debug($"queued notification {method}");
                }
                _pendingNotifications.Add(frame);
                return Task.CompletedTask;
            }
        }
        return WriteFrameAsync(frame);
    }

    private static (string Method, string Id) ParseFrameLogFields(byte[] frame)
    {
        var msg = JsonSerializer.Deserialize<JsonObject>(frame);
        return (
            msg?["method"]?.GetValue<string>() ?? "?",
            msg?["id"]?.ToJsonString() ?? "?"
        );
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var message = await _reader.ReadFrameAsync(_cts.Token);
                if (message is null) break;

                var method = message["method"]?.GetValue<string>();

                if (method is null && message["id"]?.ToJsonString() == "0" && message["result"] is not null)
                {
                    var initialized = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JsonObject() };
                    await WriteFrameAsync(SerializeFrame(initialized));
                    byte[][] pendingNotifications;
                    lock (_initLock)
                    {
                        _readiness = ServerReadiness.Initialized;
                        pendingNotifications = _pendingNotifications.ToArray();
                        _pendingNotifications.Clear();
                    }
                    if (_logger?.IsInfoEnabled == true)
                    {
                        var elapsed = (long)(System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
                        _logger.Info($"server {_solutionPath} initialized in {elapsed}ms");
                    }
                    _logger?.Debug($"draining {pendingNotifications.Length} queued notifications");
                    foreach (var f in pendingNotifications)
                        await WriteFrameAsync(f);

                    // Hard timeout: unconditional safety net for servers that never emit progress tokens.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(_hardTimeoutMs, _cts.Token);
                            int count;
                            bool stillLoading;
                            lock (_initLock)
                            {
                                stillLoading = _readiness == ServerReadiness.Initialized;
                                count = _pendingRequests.Count;
                            }
                            if (stillLoading)
                                _logger?.Info($"workspace load timeout ({_hardTimeoutMs}ms) fired as fallback, forwarding {count} queued requests");
                            _logger?.Debug("workspace ready via hard timeout");
                            await TransitionToReady();
                        }
                        catch (OperationCanceledException) { }
                    });

                    continue;
                }

                // Intercept responses to send-and-receive calls before relaying.
                var rawBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                if (message["id"] is JsonNode idNode && method is null)
                {
                    var idStr = idNode.ToJsonString();
                    if (_pending.TryGetValue(idStr, out var tcs))
                    {
                        tcs.TrySetResult(rawBytes);
                        continue;
                    }
                }

                if (method == "window/workDoneProgress/create" && message["id"] is JsonNode progressCreateId)
                {
                    var response = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = progressCreateId.DeepClone(),
                        ["result"] = JsonValue.Create<object?>(null)
                    };
                    await WriteFrameAsync(SerializeFrame(response));
                    continue;
                }

                if (method == "workspace/projectInitializationComplete")
                {
                    _logger?.Info($"server {_solutionPath} received workspace/projectInitializationComplete");
                    await TransitionToReady();
                    continue;
                }

                if (method == "$/progress")
                {
                    var token = message["params"]?["token"];
                    var value = message["params"]?["value"];
                    var kind = value?["kind"]?.GetValue<string>();
                    if (token is not null && kind is not null)
                    {
                        var tokenKey = token.ToJsonString();
                        if (kind == "begin")
                        {
                            var title = value?["title"]?.GetValue<string>() ?? "";
                            if (title.Contains("Loading", StringComparison.OrdinalIgnoreCase))
                            {
                                _loadingTokens.Add(tokenKey);
                                _seenLoadingToken = true;
                            }
                        }
                        else if (kind == "end")
                        {
                            _loadingTokens.Remove(tokenKey);
                            if (_loadingTokens.Count == 0 && _seenLoadingToken)
                                await TransitionToReady();
                        }
                    }
                    continue;
                }

                // Intercept workspace/configuration requests — respond with empty settings so Roslyn
                // doesn't stall waiting for a client that can't answer server-initiated requests.
                if (method == "workspace/configuration" && message["id"] is JsonNode configId)
                {
                    var response = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = configId.DeepClone(),
                        ["result"] = new JsonArray(new JsonObject())
                    };
                    await WriteFrameAsync(SerializeFrame(response));
                    continue;
                }

                // Relay responses and notifications back to the client (stdout of proxy)
                if (message["id"] is not null || method is not null)
                {
                    if (OnRelayFrame is { } relay)
                    {
                        if (_logger?.IsDebugEnabled == true && method is null && message["id"] is JsonNode relayId)
                            _logger.Debug($"relay response id={relayId.ToJsonString()}");
                        await relay(rawBytes);
                    }
                    else
                        _logger?.Info($"server {_solutionPath}: no relay subscriber, frame dropped");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoslynServerProcess] reader error: {ex.Message}");
        }
    }

    private async Task TransitionToReady()
    {
        byte[][] pending;
        lock (_initLock)
        {
            if (_readiness != ServerReadiness.Initialized) return;
            _readiness = ServerReadiness.Ready;
            pending = _pendingRequests.ToArray();
            _pendingRequests.Clear();
        }
        if (_logger?.IsInfoEnabled == true)
        {
            var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
            _logger.Info($"server {_solutionPath} workspace ready in {elapsed}ms");
        }
        _logger?.Debug($"draining {pending.Length} queued requests");
        foreach (var f in pending)
            await WriteFrameAsync(f);
    }

    private async Task WriteFrameAsync(byte[] frame)
    {
        await _writeLock.WaitAsync();
        try
        {
            var header = Encoding.UTF8.GetBytes($"Content-Length: {frame.Length}\r\n\r\n");
            await _stdin.WriteAsync(header);
            await _stdin.WriteAsync(frame);
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

    private static byte[] SerializeFrame(JsonObject message)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

    public async Task<byte[]> SendAndReceiveAsync(byte[] frame)
    {
        _cts.Token.ThrowIfCancellationRequested();

        // Rewrite the request id to a synthetic one so we can correlate the response.
        var request = JsonSerializer.Deserialize<JsonObject>(frame)!;
        var syntheticId = $"__mux_{Interlocked.Increment(ref _syntheticIdCounter)}";
        request["id"] = syntheticId;
        var rewritten = SerializeFrame(request);

        // ToJsonString() preserves the JSON-quoted form that ReadLoopAsync extracts from responses.
        var pendingKey = request["id"]!.ToJsonString();

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        await WriteFrameAsync(SerializeFrame(shutdown));

        var exit = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "exit"
        };
        await WriteFrameAsync(SerializeFrame(exit));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync();

        try { await _readerTask; } catch { }

        lock (_initLock)
        {
            _pendingNotifications.Clear();
            _pendingRequests.Clear();
        }

        if (_onDispose is not null)
            await _onDispose();

        _cts.Dispose();
        _writeLock.Dispose();
    }

}
