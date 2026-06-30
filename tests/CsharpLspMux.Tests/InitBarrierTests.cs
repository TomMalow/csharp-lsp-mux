using Xunit;

namespace CsharpLspMux.Tests;

public sealed class InitBarrierTests
{
    private static InitBarrier MakeBarrier() => new();

    [Fact]
    public void DrainPending_AfterSignal_ReturnsEnqueuedFramesInOrder()
    {
        var gate = MakeBarrier();
        var frame1 = new byte[] { 1 };
        var frame2 = new byte[] { 2 };

        gate.Enqueue(frame1);
        gate.Enqueue(frame2);
        gate.SignalInitialized();

        var drained = gate.DrainPending().ToList();
        Assert.Equal(2, drained.Count);
        Assert.Same(frame1, drained[0]);
        Assert.Same(frame2, drained[1]);
    }

    [Fact]
    public async Task WaitInitializedAsync_CompletesAfterSignal()
    {
        var gate = MakeBarrier();
        var waitTask = gate.WaitInitializedAsync();

        Assert.False(waitTask.IsCompleted);

        gate.SignalInitialized();
        await waitTask;

        Assert.True(waitTask.IsCompleted);
    }

    [Fact]
    public async Task WaitInitializedAsync_AlreadySignaled_CompletesImmediately()
    {
        var gate = MakeBarrier();
        gate.SignalInitialized();

        await gate.WaitInitializedAsync();
        // Completes without hanging
    }

    [Fact]
    public void DrainPending_CalledTwice_SecondCallEmpty()
    {
        var gate = MakeBarrier();
        gate.Enqueue(new byte[] { 1 });
        gate.SignalInitialized();

        _ = gate.DrainPending().ToList();
        var second = gate.DrainPending().ToList();

        Assert.Empty(second);
    }

}
