namespace CsharpLspMux;

/// <summary>Maps in-flight request IDs to the child server that owns them, enabling cancel routing and eviction cleanup.</summary>
internal sealed class RequestLedger
{
    private readonly Dictionary<string, IChildServer> _owners = new();

    public void Register(string key, IChildServer server) => _owners[key] = server;

    public IChildServer? Lookup(string key) => _owners.TryGetValue(key, out var s) ? s : null;

    public void Remove(string key) => _owners.Remove(key);

    public void EvictServer(IChildServer server)
    {
        foreach (var key in _owners.Keys.Where(k => _owners[k] == server).ToList())
            _owners.Remove(key);
    }
}
