namespace CsharpLspMux;

/// <summary>Writes Content-Length-framed LSP messages to a stream.</summary>
public interface IFrameWriter
{
    Task WriteFrameAsync(Frame frame, CancellationToken ct = default);
}
