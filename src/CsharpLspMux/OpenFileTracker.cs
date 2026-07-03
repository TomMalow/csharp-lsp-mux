namespace CsharpLspMux;

/// <summary>Tracks which URIs have been opened on each child server, used to synthesize didOpen when routing a request for an unopened file.</summary>
internal sealed class OpenFileTracker
{
    private readonly Dictionary<IChildServer, HashSet<string>> _opened = new();
    private readonly object _lock = new();

    public void MarkOpened(IChildServer server, string uri)
    {
        lock (_lock)
        {
            if (!_opened.TryGetValue(server, out var set))
                _opened[server] = set = new HashSet<string>();
            set.Add(uri);
        }
    }

    public void MarkClosed(IChildServer server, string uri)
    {
        lock (_lock)
        {
            if (_opened.TryGetValue(server, out var set))
                set.Remove(uri);
        }
    }

    public bool IsOpened(IChildServer server, string uri)
    {
        lock (_lock)
            return _opened.TryGetValue(server, out var set) && set.Contains(uri);
    }

    public void EvictServer(IChildServer server)
    {
        lock (_lock)
            _opened.Remove(server);
    }
}
