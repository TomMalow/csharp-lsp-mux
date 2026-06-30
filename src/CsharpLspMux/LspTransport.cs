using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

/// <summary>
/// Content-Length framed LSP transport. Owns the write stream and serializes concurrent writes.
/// </summary>
public sealed class LspTransport : ILspTransport
{
    private readonly Stream _writeStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LspTransport(Stream writeStream)
    {
        _writeStream = writeStream;
    }

    public static async Task<JsonObject?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        int contentLength = -1;

        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (line is null) return null;
            if (line.Length == 0) break;
            if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length: ".Length..], out var len))
                contentLength = len;
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

    public async Task WriteFrameAsync(byte[] frame)
    {
        var header = Encoding.UTF8.GetBytes($"Content-Length: {frame.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _writeStream.WriteAsync(header);
            await _writeStream.WriteAsync(frame);
            await _writeStream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SendResponseAsync(JsonNode? id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        await WriteFrameAsync(body);
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
}
