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
public sealed class RoslynServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Stream _responseOut;

    private readonly TaskCompletionSource _initializedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<byte[]> _pendingQueue = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerTask;

    public bool IsInitialized => _initializedTcs.Task.IsCompleted;

    private RoslynServerProcess(Process process, Stream responseOut)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        _responseOut = responseOut;
        _readerTask = Task.Run(ReadLoopAsync);
    }

    public static RoslynServerProcess Start(string solutionPath, Stream responseOut)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        var psi = new ProcessStartInfo("roslyn-language-server")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = solutionDir,
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start roslyn-language-server");
        var server = new RoslynServerProcess(process, responseOut);
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

    /// <summary>
    /// Forwards a raw JSON-RPC request frame. Queued until initialized if not yet ready.
    /// </summary>
    public async Task ForwardRequestAsync(byte[] frame)
    {
        if (!IsInitialized)
        {
            _pendingQueue.Enqueue(frame);
            await _initializedTcs.Task;
            return;
        }

        await WriteFrameAsync(frame);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_stdout, _cts.Token);
                if (message is null) break;

                var method = message["method"]?.GetValue<string>();

                if (method == "initialized")
                {
                    _initializedTcs.TrySetResult();
                    await FlushPendingAsync();
                    continue;
                }

                // Relay responses and notifications back to the client (stdout of proxy)
                if (message["id"] is not null || method is not null)
                {
                    var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                    await WriteResponseToClientAsync(raw);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoslynServerProcess] reader error: {ex.Message}");
        }
    }

    private async Task FlushPendingAsync()
    {
        while (_pendingQueue.TryDequeue(out var frame))
            await WriteFrameAsync(frame);
    }

    private async Task WriteResponseToClientAsync(byte[] body)
    {
        var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _responseOut.WriteAsync(header);
            await _responseOut.WriteAsync(body);
            await _responseOut.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
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

    private static async Task<JsonObject?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        int contentLength = -1;

        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (line is null) return null;
            if (line.Length == 0) break;
            if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length: ".Length..]);
        }

        if (contentLength < 0) return null;

        var buffer = new byte[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0) return null;
            totalRead += read;
        }

        return JsonSerializer.Deserialize<JsonObject>(buffer);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
            if (read == 0) return null;
            var ch = (char)buf[0];
            if (ch == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(ch);
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
        await _cts.CancelAsync();

        if (!_process.HasExited)
        {
            try { await ShutdownAsync(); } catch { }
            await Task.Delay(500);
            if (!_process.HasExited)
                _process.Kill();
        }

        try { await _readerTask; } catch { }
        _process.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
