namespace CsharpLspMux;

public interface IServerPool<T> where T : IAsyncDisposable
{
    Task<T> GetOrAddAsync(string key);
    IEnumerable<T> ActiveServers { get; }
    Task DisposeAllAsync();
}
