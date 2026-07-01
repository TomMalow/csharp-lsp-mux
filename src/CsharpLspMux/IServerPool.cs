namespace CsharpLspMux;

public interface IServerPool<T> where T : IAsyncDisposable
{
    event Action<T>? Evicted;
    Task<T> GetOrAddAsync(string key);
    IEnumerable<T> ActiveServers { get; }
    Task DisposeAllAsync();
}
