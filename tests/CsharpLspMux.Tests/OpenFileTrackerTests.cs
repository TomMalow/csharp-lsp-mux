using Xunit;

namespace CsharpLspMux.Tests;

public class OpenFileTrackerTests
{
    private sealed class StubServer : IChildServer
    {
        public ServerReadiness Readiness => ServerReadiness.Ready;
        public event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame { add { } remove { } }
        public Task ForwardRequestAsync(byte[] frame) => Task.CompletedTask;
        public Task ForwardNotificationAsync(byte[] frame) => Task.CompletedTask;
        public Task<byte[]> SendAndReceiveAsync(byte[] frame) => Task.FromResult(Array.Empty<byte>());
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void MarkOpened_ThenIsOpened_ReturnsTrue()
    {
        var tracker = new OpenFileTracker();
        var server = new StubServer();

        tracker.MarkOpened(server, "file:///a.cs");

        Assert.True(tracker.IsOpened(server, "file:///a.cs"));
    }

    [Fact]
    public void MarkClosed_AfterMarkOpened_IsOpenedReturnsFalse()
    {
        var tracker = new OpenFileTracker();
        var server = new StubServer();
        tracker.MarkOpened(server, "file:///a.cs");

        tracker.MarkClosed(server, "file:///a.cs");

        Assert.False(tracker.IsOpened(server, "file:///a.cs"));
    }

    [Fact]
    public void IsOpened_UnknownServer_ReturnsFalse()
    {
        var tracker = new OpenFileTracker();
        var server = new StubServer();

        Assert.False(tracker.IsOpened(server, "file:///a.cs"));
    }

    [Fact]
    public void EvictServer_RemovesAllUris_IsOpenedReturnsFalse()
    {
        var tracker = new OpenFileTracker();
        var server = new StubServer();
        tracker.MarkOpened(server, "file:///a.cs");
        tracker.MarkOpened(server, "file:///b.cs");

        tracker.EvictServer(server);

        Assert.False(tracker.IsOpened(server, "file:///a.cs"));
        Assert.False(tracker.IsOpened(server, "file:///b.cs"));
    }

    [Fact]
    public async Task ConcurrentMarkOpened_SameUri_DoesNotThrow_IsOpenedReturnsTrue()
    {
        var tracker = new OpenFileTracker();
        var server = new StubServer();
        const string uri = "file:///concurrent.cs";

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => tracker.MarkOpened(server, uri)));
        await Task.WhenAll(tasks);

        Assert.True(tracker.IsOpened(server, uri));
    }
}
