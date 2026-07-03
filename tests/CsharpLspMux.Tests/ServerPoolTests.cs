using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public sealed class ServerPoolTests
{
    // --- fake ---

    private sealed class FakeServer : IChildServer
    {
        public int DisposeCount;
        public int ShutdownCount;
        public int ShutdownBeforeDisposeCount;
        public byte[]? LastSentFrame;
        public ServerReadiness Readiness => ServerReadiness.Ready;
        public event Func<ReadOnlyMemory<byte>, ValueTask>? OnRelayFrame { add { } remove { } }

        public Task ForwardRequestAsync(byte[] frame) => Task.CompletedTask;
        public Task ForwardNotificationAsync(byte[] frame) => Task.CompletedTask;

        public Task<byte[]> SendAndReceiveAsync(byte[] frame)
        {
            LastSentFrame = frame;
            // Echo back a minimal JSON-RPC result response with same id
            var req = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(frame)!;
            var id = req["id"];
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = new System.Text.Json.Nodes.JsonArray()
            };
            var body = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(resp));
            return Task.FromResult(body);
        }

        public Task ShutdownAsync()
        {
            ShutdownCount++;
            if (DisposeCount == 0) ShutdownBeforeDisposeCount++;
            return Task.CompletedTask;
        }

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
    public async Task SendAndReceiveAsync_ReturnsResponseFrame()
    {
        var pool = MakePool(10);
        var server = await pool.GetOrAddAsync("slnA");

        var req = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":42,"method":"workspace/symbol","params":{"query":"Foo"}}""");
        var response = await server.SendAndReceiveAsync(req);

        Assert.NotNull(response);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(response)!;
        Assert.Equal(42, parsed["id"]?.GetValue<int>());
    }

    [Fact]
    public async Task ActiveServers_ReturnsLiveEntries()
    {
        var pool = MakePool(10);
        var a = await pool.GetOrAddAsync("slnA");
        var b = await pool.GetOrAddAsync("slnB");

        var active = pool.ActiveServers.ToList();

        Assert.Equal(2, active.Count);
        Assert.Contains(a, active);
        Assert.Contains(b, active);
    }

    [Fact]
    public async Task ActiveServers_EvictedEntry_NotIncluded()
    {
        var pool = MakePool(2);
        var a = await pool.GetOrAddAsync("slnA");
        await pool.GetOrAddAsync("slnB");
        await pool.GetOrAddAsync("slnC"); // evicts slnA

        var active = pool.ActiveServers.ToList();

        Assert.Equal(2, active.Count);
        Assert.DoesNotContain(a, active);
    }

    [Fact]
    public async Task OnEviction_CalledWithEvictedServer_AfterDispose()
    {
        FakeServer? handlerArg = null;
        int disposeCountInHandler = -1;
        var pool = MakePool(2);
        pool.OnEviction = s =>
        {
            handlerArg = s;
            disposeCountInHandler = s.DisposeCount;
            return Task.CompletedTask;
        };

        var a = await pool.GetOrAddAsync("slnA");
        await pool.GetOrAddAsync("slnB");
        await pool.GetOrAddAsync("slnC"); // evicts slnA

        Assert.Same(a, handlerArg);
        Assert.Equal(1, disposeCountInHandler); // handler ran after DisposeAsync
    }

    [Fact]
    public async Task OnEviction_HandlerThrows_DoesNotPropagateToPool()
    {
        var pool = MakePool(2);
        pool.OnEviction = _ => throw new InvalidOperationException("handler error");

        await pool.GetOrAddAsync("slnA");
        await pool.GetOrAddAsync("slnB");

        var ex = await Record.ExceptionAsync(() => pool.GetOrAddAsync("slnC"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task OnEviction_HandlerThrows_LogsError()
    {
        var logOutput = new StringWriter();
        var logger = new MuxLogger(enabled: true, logOutput);
        var pool = new ServerPool<FakeServer>(cap: 2, factory: _ => Task.FromResult(new FakeServer()), logger: logger);
        pool.OnEviction = _ => throw new InvalidOperationException("boom");

        await pool.GetOrAddAsync("slnA");
        await pool.GetOrAddAsync("slnB");
        await pool.GetOrAddAsync("slnC"); // evicts slnA, handler throws

        Assert.Contains("boom", logOutput.ToString());
    }

    [Fact]
    public async Task Integration_LruOverflow_HandlerCalledForEachEviction()
    {
        var evictedServers = new List<FakeServer>();
        var pool = new ServerPool<FakeServer>(cap: 2, factory: _ => Task.FromResult(new FakeServer()));
        pool.OnEviction = s => { evictedServers.Add(s); return Task.CompletedTask; };

        var a = await pool.GetOrAddAsync("slnA");
        var b = await pool.GetOrAddAsync("slnB");
        await pool.GetOrAddAsync("slnC"); // evicts slnA

        Assert.Single(evictedServers);
        Assert.Same(a, evictedServers[0]);
        Assert.Equal(1, a.DisposeCount);

        await pool.GetOrAddAsync("slnD"); // evicts slnB

        Assert.Equal(2, evictedServers.Count);
        Assert.Same(b, evictedServers[1]);
        Assert.Equal(1, b.DisposeCount);
    }

    [Fact]
    public async Task DisposeAll_CallsShutdownBeforeDispose_OnEachServer()
    {
        var pool = MakePool(10);
        pool.OnGracefulShutdown = s => s.ShutdownAsync();
        var a = await pool.GetOrAddAsync("slnA");
        var b = await pool.GetOrAddAsync("slnB");

        await pool.DisposeAllAsync();

        Assert.Equal(1, a.ShutdownCount);
        Assert.Equal(1, b.ShutdownCount);
        Assert.Equal(1, a.ShutdownBeforeDisposeCount);
        Assert.Equal(1, b.ShutdownBeforeDisposeCount);
    }

    [Fact]
    public async Task Evict_DoesNotCallShutdown()
    {
        var pool = MakePool(2);
        pool.OnGracefulShutdown = s => s.ShutdownAsync();
        var a = await pool.GetOrAddAsync("slnA");
        await pool.GetOrAddAsync("slnB");

        await pool.GetOrAddAsync("slnC"); // evicts slnA (LRU)

        Assert.Equal(0, a.ShutdownCount);
        Assert.Equal(1, a.DisposeCount);
    }

}
