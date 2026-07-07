namespace CsharpLspMux;

public interface IFrameReader
{
    Task<Frame?> ReadFrameAsync(CancellationToken ct = default);
}
