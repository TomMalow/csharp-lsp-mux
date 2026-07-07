using Xunit;

namespace CsharpLspMux.Tests;

public class RequestLedgerTests
{
    private sealed class StubServer : IChildServer
    {
        public ServerReadiness Readiness => ServerReadiness.Ready;
        public event Func<Frame, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(Frame frame) => Task.CompletedTask;
        public Task ForwardNotificationAsync(Frame frame) => Task.CompletedTask;
        public Task<Frame> SendAndReceiveAsync(Frame frame) => Task.FromResult(Frame.FromJson(new System.Text.Json.Nodes.JsonObject()));
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
