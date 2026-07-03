namespace CsharpLspMux;

/// <summary>Writes Content-Length-framed LSP messages to a stream.</summary>
public interface IFrameWriter
{
    Task WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);
}
