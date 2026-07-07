namespace CsharpLspMux;

/// <summary>
/// The server pool's per-entry unit: one child server plus the session state the mux keeps for
/// it (which URIs it has opened, which request ids are in flight). This state lives and dies
/// with the session, so pool eviction/drain cleans it up structurally.
/// </summary>
public sealed class ServerSession : IAsyncDisposable
{
    private readonly HashSet<string> _openedUris = new();
    private readonly HashSet<string> _inFlightIds = new();
    private readonly object _lock = new();

    public ServerSession(IChildServer server) => Server = server;

    public IChildServer Server { get; }

    public void MarkOpened(string uri)
    {
        lock (_lock) _openedUris.Add(uri);
    }

    public void MarkClosed(string uri)
    {
        lock (_lock) _openedUris.Remove(uri);
    }

    public bool IsOpened(string uri)
    {
        lock (_lock) return _openedUris.Contains(uri);
    }

    public void Register(string id)
    {
        lock (_lock) _inFlightIds.Add(id);
    }

    public void Remove(string id)
    {
        lock (_lock) _inFlightIds.Remove(id);
    }

    public bool OwnsRequest(string id)
    {
        lock (_lock) return _inFlightIds.Contains(id);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        lock (_lock)
        {
            _openedUris.Clear();
            _inFlightIds.Clear();
        }
    }
}
