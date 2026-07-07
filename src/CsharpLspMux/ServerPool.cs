namespace CsharpLspMux;

/// <summary>
/// Bounded LRU pool of child servers keyed by solution path.
/// When full, least-recently-used entry is evicted via DisposeAsync.
/// </summary>
public sealed class ServerPool<TServer> : IServerPool<TServer> where TServer : IAsyncDisposable
{
    private readonly int _cap;
    private readonly Func<string, Task<TServer>> _factory;

    // Ordered by recency: last = most recently used.
    private readonly LinkedList<(string Key, TServer Server)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, TServer Server)>> _index = new();

    /// <summary>
    /// Called before DisposeAsync on each server during session drain. Use for graceful LSP shutdown.
    /// Not called on LRU eviction.
    /// </summary>
    public Func<TServer, Task>? OnGracefulShutdown { get; set; }

    public ServerPool(int cap, Func<string, Task<TServer>> factory)
    {
        _cap = cap;
        _factory = factory;
    }

    public IEnumerable<TServer> ActiveSessions => _lru.Select(e => e.Server);

    public async Task<TServer> GetOrAddAsync(string key)
    {
        if (_index.TryGetValue(key, out var node))
        {
            // Promote to MRU position
            _lru.Remove(node);
            _lru.AddLast(node);
            return node.Value.Server;
        }

        // Create first: if the factory throws, pool state is unchanged (no orphaned eviction).
        var server = await _factory(key);

        if (_lru.Count >= _cap)
            await EvictLruAsync();

        var newNode = _lru.AddLast((key, server));
        _index[key] = newNode;
        return server;
    }

    public async Task DisposeAllAsync()
    {
        var servers = _lru.Select(e => e.Server).ToList();
        _lru.Clear();
        _index.Clear();
        await Task.WhenAll(servers.Select(async s =>
        {
            if (OnGracefulShutdown is { } shutdown)
                try { await shutdown(s); } catch { }
            try { await s.DisposeAsync(); } catch { }
        }));
    }

    private async Task EvictLruAsync()
    {
        var oldest = _lru.First!;
        _lru.RemoveFirst();
        _index.Remove(oldest.Value.Key);
        try { await oldest.Value.Server.DisposeAsync(); } catch { }
    }
}
