using System.Text;

namespace CsharpLspMux;

/// <summary>
/// Content-Length framed LSP transport. Owns the write stream and serializes concurrent writes.
/// </summary>
public sealed class LspTransport : IFrameWriter
{
    private readonly Stream _writeStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LspTransport(Stream writeStream)
    {
        _writeStream = writeStream;
    }

    public async Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        var header = Encoding.UTF8.GetBytes($"Content-Length: {frame.Length}\r\n\r\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            await _writeStream.WriteAsync(header, ct);
            await _writeStream.WriteAsync(frame, ct);
            await _writeStream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
