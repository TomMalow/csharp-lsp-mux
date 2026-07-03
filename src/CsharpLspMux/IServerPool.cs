namespace CsharpLspMux;

public interface IServerPool<T> where T : IAsyncDisposable
{
    /// <summary>
    /// Called after a server is disposed during LRU eviction. Not called during <see cref="DisposeAllAsync"/>.
    /// </summary>
    Func<T, Task>? OnEviction { get; set; }
    Task<T> GetOrAddAsync(string key);
    IEnumerable<T> ActiveServers { get; }
    Task DisposeAllAsync();
}
