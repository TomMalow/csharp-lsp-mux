using Xunit;

namespace CsharpLspMux.Tests;

public class RequestLedgerTests
{
    private sealed class StubServer : IChildServer
    {
        public bool IsInitialized => true;
        public event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(byte[] frame) => Task.CompletedTask;
        public Task<byte[]> SendAndReceiveAsync(byte[] frame) => Task.FromResult(Array.Empty<byte>());
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Register_ThenLookup_ReturnsServer()
    {
        var ledger = new RequestLedger();
        var server = new StubServer();

        ledger.Register("1", server);

        Assert.Same(server, ledger.Lookup("1"));
    }

    [Fact]
    public void Lookup_UnknownKey_ReturnsNull()
    {
        var ledger = new RequestLedger();

        Assert.Null(ledger.Lookup("missing"));
    }

    [Fact]
    public void Remove_RemovesEntry_LookupReturnsNull()
    {
        var ledger = new RequestLedger();
        var server = new StubServer();
        ledger.Register("2", server);

        ledger.Remove("2");

        Assert.Null(ledger.Lookup("2"));
    }

    [Fact]
    public void EvictServer_RemovesAllEntriesForEvictedServer()
    {
        var ledger = new RequestLedger();
        var server = new StubServer();
        var other = new StubServer();
        ledger.Register("a", server);
        ledger.Register("b", server);
        ledger.Register("c", other);

        ledger.EvictServer(server);

        Assert.Null(ledger.Lookup("a"));
        Assert.Null(ledger.Lookup("b"));
        Assert.Same(other, ledger.Lookup("c"));
    }

    [Fact]
    public void EvictServer_UnknownServer_IsNoOp()
    {
        var ledger = new RequestLedger();
        var server = new StubServer();
        ledger.Register("x", new StubServer());

        ledger.EvictServer(server); // should not throw

        Assert.NotNull(ledger.Lookup("x"));
    }
}
