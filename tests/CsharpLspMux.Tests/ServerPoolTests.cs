using Xunit;

namespace CsharpLspMux.Tests;

public sealed class ServerPoolTests
{
    // --- fake ---

    private sealed class FakeServer : IChildServer
    {
        public int DisposeCount;
        public Task ForwardRequestAsync(byte[] frame) => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    // --- helpers ---

    private static ServerPool<FakeServer> MakePool(int cap)
        => new(cap, _ => Task.FromResult(new FakeServer()));

    // --- tests ---

    [Fact]
    public async Task GetOrAdd_FirstCall_CreatesServer()
    {
        var pool = MakePool(10);
        var server = await pool.GetOrAddAsync("slnA");
        Assert.NotNull(server);
    }

    [Fact]
    public async Task GetOrAdd_SecondCall_ReturnsSameInstance()
    {
        var pool = MakePool(10);
        var a = await pool.GetOrAddAsync("slnA");
        var b = await pool.GetOrAddAsync("slnA");
        Assert.Same(a, b);
    }

    [Fact]
    public async Task AtCap_NewKey_EvictsLeastRecentlyUsed()
    {
        var pool = MakePool(2);
        var a = await pool.GetOrAddAsync("slnA");
        var b = await pool.GetOrAddAsync("slnB");

        // slnC overflows the cap — slnA is LRU
        await pool.GetOrAddAsync("slnC");

        Assert.Equal(1, a.DisposeCount); // slnA evicted
        Assert.Equal(0, b.DisposeCount); // slnB survived
    }

    [Fact]
    public async Task RecentAccess_ProtectsFromEviction()
    {
        var pool = MakePool(2);
        var a = await pool.GetOrAddAsync("slnA");
        var b = await pool.GetOrAddAsync("slnB");

        // Touch slnA — promotes to MRU, making slnB the LRU
        await pool.GetOrAddAsync("slnA");

        // slnC overflows — slnB should be evicted, not slnA
        await pool.GetOrAddAsync("slnC");

        Assert.Equal(0, a.DisposeCount); // slnA survived (was MRU)
        Assert.Equal(1, b.DisposeCount); // slnB evicted (became LRU after slnA touch)
    }

    [Fact]
    public async Task DisposeAll_DisposesEveryServer()
    {
        var pool = MakePool(10);
        var a = await pool.GetOrAddAsync("slnA");
        var b = await pool.GetOrAddAsync("slnB");

        await pool.DisposeAllAsync();

        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
    }

    [Fact]
    public async Task FromEnvironment_UsesEnvVar()
    {
        Environment.SetEnvironmentVariable("LSP_ROUTER_MAX_SERVERS", "2");
        try
        {
            var pool = ServerPool<FakeServer>.FromEnvironment(_ => Task.FromResult(new FakeServer()));
            var a = await pool.GetOrAddAsync("slnA");
            var b = await pool.GetOrAddAsync("slnB");
            await pool.GetOrAddAsync("slnC"); // overflows a cap-2 pool

            Assert.Equal(1, a.DisposeCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LSP_ROUTER_MAX_SERVERS", null);
        }
    }
}
