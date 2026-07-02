using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

/// <summary>
/// Wraps a single roslyn-language-server child process.
/// Queues outbound requests until the server signals <c>initialized</c>, then flushes.
/// </summary>
public sealed class RoslynServerProcess : IChildServer
{
    private readonly Stream _stdin;
    private readonly IFrameReader _reader;
    private readonly ILspTransport _clientTransport;
    private readonly Func<Task>? _onDispose;

    private readonly object _initLock = new();
    private bool _initialized;
    private readonly List<byte[]> _pendingQueue = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerTask;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pending = new();
    private int _syntheticIdCounter;
    private int _disposed;

    public bool IsInitialized { get { lock (_initLock) return _initialized; } }

    private RoslynServerProcess(Stream stdin, IFrameReader reader, ILspTransport clientTransport, Func<Task>? onDispose)
    {
        _stdin = stdin;
        _reader = reader;
        _clientTransport = clientTransport;
        _onDispose = onDispose;
        _readerTask = Task.Run(ReadLoopAsync);
    }

    internal static RoslynServerProcess CreateForTest(
        Stream stdin,
        IFrameReader reader,
        ILspTransport clientTransport,
        Func<Task>? onDispose = null)
        => new(stdin, reader, clientTransport, onDispose);

    public static RoslynServerProcess Start(string solutionPath, ILspTransport clientTransport)
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
            clientTransport,
            async () =>
            {
                if (!process.HasExited)
                {
                    if (!process.WaitForExit(500))
                        process.Kill();
                }
                process.Dispose();
            });
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
                ["capabilities"] = new JsonObject(),
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
            if (!_initialized)
            {
                _pendingQueue.Add(frame);
                return Task.CompletedTask;
            }
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

                var method = message["method"]?.GetValue<string>();

                if (method is null && message["id"]?.ToJsonString() == "0" && message["result"] is not null)
                {
                    var initialized = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JsonObject() };
                    await WriteFrameAsync(SerializeFrame(initialized));
                    byte[][] pending;
                    lock (_initLock)
                    {
                        _initialized = true;
                        pending = _pendingQueue.ToArray();
                        _pendingQueue.Clear();
                    }
                    foreach (var f in pending)
                        await WriteFrameAsync(f);
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

                // Relay responses and notifications back to the client (stdout of proxy)
                if (message["id"] is not null || method is not null)
                    await _clientTransport.WriteFrameAsync(rawBytes);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoslynServerProcess] reader error: {ex.Message}");
        }
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

        lock (_initLock) { _pendingQueue.Clear(); }

        if (_onDispose is not null)
            await _onDispose();

        _cts.Dispose();
        _writeLock.Dispose();
    }

}
