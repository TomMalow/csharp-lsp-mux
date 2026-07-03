namespace CsharpLspMux;

public interface IChildServer : IAsyncDisposable
{
    /// <summary>Current lifecycle state of this server.</summary>
    ServerReadiness Readiness { get; }
    /// <summary>
    /// Fired by the server's read loop for every frame that must be relayed to the client.
    /// Subscribers wire this to the client transport at the composition root.
    /// </summary>
    event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame;
    /// <summary>Forwards a request (has an id) to the child server. Gates on <see cref="ServerReadiness.Initialized"/>.</summary>
    Task ForwardRequestAsync(byte[] frame);
    /// <summary>Forwards a notification (no id) to the child server. Gates on <see cref="ServerReadiness.Initialized"/>.</summary>
    Task ForwardNotificationAsync(byte[] frame);
    /// <summary>
    /// Sends a request and returns the raw response frame. Used for workspace/symbol broadcast.
    /// </summary>
    Task<byte[]> SendAndReceiveAsync(byte[] frame);
    /// <summary>Sends LSP shutdown/exit to the child process and waits for it to stop.</summary>
    Task ShutdownAsync();
}
