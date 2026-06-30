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
}
