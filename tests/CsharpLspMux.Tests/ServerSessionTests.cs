using Xunit;

namespace CsharpLspMux.Tests;

public class ServerSessionTests
{
    private sealed class StubServer : IChildServer
    {
        public int DisposeCount;
        public ServerReadiness Readiness => ServerReadiness.Ready;
        public event Func<Frame, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(Frame frame) => Task.CompletedTask;
        public Task ForwardNotificationAsync(Frame frame) => Task.CompletedTask;
        public Task<Frame> SendAndReceiveAsync(Frame frame) => Task.FromResult(Frame.FromJson(new System.Text.Json.Nodes.JsonObject()));
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void MarkOpened_ThenIsOpened_ReturnsTrue()
    {
        var session = new ServerSession(new StubServer());

        session.MarkOpened("file:///a.cs");

        Assert.True(session.IsOpened("file:///a.cs"));
    }

    [Fact]
    public void MarkClosed_AfterMarkOpened_IsOpenedReturnsFalse()
    {
        var session = new ServerSession(new StubServer());
        session.MarkOpened("file:///a.cs");

        session.MarkClosed("file:///a.cs");

        Assert.False(session.IsOpened("file:///a.cs"));
    }

    [Fact]
    public void IsOpened_UnknownUri_ReturnsFalse()
    {
        var session = new ServerSession(new StubServer());

        Assert.False(session.IsOpened("file:///a.cs"));
    }

    [Fact]
    public async Task ConcurrentMarkOpened_SameUri_DoesNotThrow_IsOpenedReturnsTrue()
    {
        var session = new ServerSession(new StubServer());
        const string uri = "file:///concurrent.cs";

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => session.MarkOpened(uri)));
        await Task.WhenAll(tasks);

        Assert.True(session.IsOpened(uri));
    }

    [Fact]
    public void Register_ThenOwnsRequest_ReturnsTrue()
    {
        var session = new ServerSession(new StubServer());

        session.Register("1");

        Assert.True(session.OwnsRequest("1"));
    }

    [Fact]
    public void OwnsRequest_UnknownId_ReturnsFalse()
    {
        var session = new ServerSession(new StubServer());

        Assert.False(session.OwnsRequest("missing"));
    }

    [Fact]
    public void Remove_RemovesEntry_OwnsRequestReturnsFalse()
    {
        var session = new ServerSession(new StubServer());
        session.Register("2");

        session.Remove("2");

        Assert.False(session.OwnsRequest("2"));
    }

    [Fact]
    public async Task DisposeAsync_DisposesInnerServer()
    {
        var server = new StubServer();
        var session = new ServerSession(server);

        await session.DisposeAsync();

        Assert.Equal(1, server.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_DropsBothSets()
    {
        var session = new ServerSession(new StubServer());
        session.MarkOpened("file:///a.cs");
        session.Register("1");

        await session.DisposeAsync();

        Assert.False(session.IsOpened("file:///a.cs"));
        Assert.False(session.OwnsRequest("1"));
    }
}
