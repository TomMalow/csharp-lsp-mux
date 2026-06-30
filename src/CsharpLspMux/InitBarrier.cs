using System.Collections.Concurrent;

namespace CsharpLspMux;

internal sealed class InitBarrier
{
    private readonly TaskCompletionSource _initializedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<byte[]> _queue = new();

    public void Enqueue(byte[] frame) => _queue.Enqueue(frame);

    public Task WaitInitializedAsync() => _initializedTcs.Task;

    public void SignalInitialized() => _initializedTcs.TrySetResult();

    public bool IsInitialized => _initializedTcs.Task.IsCompleted;

    public IEnumerable<byte[]> DrainPending()
    {
        while (_queue.TryDequeue(out var frame))
            yield return frame;
    }
}
