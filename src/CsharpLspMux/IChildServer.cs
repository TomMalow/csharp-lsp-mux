namespace CsharpLspMux;

public interface IChildServer : IAsyncDisposable
{
    bool IsInitialized { get; }
    /// <summary>
    /// Fired by the server's read loop for every frame that must be relayed to the client.
    /// Subscribers wire this to the client transport at the composition root.
    /// </summary>
    event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame;
    Task ForwardRequestAsync(byte[] frame);
    /// <summary>
    /// Sends a request and returns the raw response frame. Used for workspace/symbol broadcast.
    /// </summary>
    Task<byte[]> SendAndReceiveAsync(byte[] frame);
    Task ShutdownAsync();
}
