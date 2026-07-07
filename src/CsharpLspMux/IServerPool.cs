namespace CsharpLspMux;

public interface IServerPool<T> where T : IAsyncDisposable
{
    Task<T> GetOrAddAsync(string key);
    IEnumerable<T> ActiveSessions { get; }
    Task DisposeAllAsync();
}
