namespace CsharpLspMux;

public interface IChildServer : IAsyncDisposable
{
    Task ForwardRequestAsync(byte[] frame);
    /// <summary>
    /// Sends a request and returns the raw response frame. Used for workspace/symbol broadcast.
    /// </summary>
    Task<byte[]> SendAndReceiveAsync(byte[] frame);
    Task ShutdownAsync();
}
