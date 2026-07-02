namespace CsharpLspMux;

public interface IChildServer : IAsyncDisposable
{
    bool IsInitialized { get; }
    Task ForwardRequestAsync(byte[] frame);
    /// <summary>
    /// Sends a request and returns the raw response frame. Used for workspace/symbol broadcast.
    /// </summary>
    Task<byte[]> SendAndReceiveAsync(byte[] frame);
    Task ShutdownAsync();
}
