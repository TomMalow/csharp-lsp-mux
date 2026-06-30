namespace CsharpLspMux;

public interface IChildServer : IAsyncDisposable
{
    Task ForwardRequestAsync(byte[] frame);
    Task ShutdownAsync();
}
