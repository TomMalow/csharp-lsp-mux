namespace CsharpLspMux;

/// <summary>Whether an outbound frame is a request (has an id) or a notification.</summary>
public enum FrameKind
{
    Request,
    Notification,
}

/// <summary>Whether <see cref="WorkspaceReadiness.Gate"/> allows a frame through immediately or queues it.</summary>
public enum GateDecision
{
    SendNow,
    Queued,
}

/// <summary>What <see cref="WorkspaceReadiness.Observe"/> caused, if anything.</summary>
public enum ReadinessTransition
{
    None,
    BecameInitialized,
    BecameReady,
}

/// <summary>A typed event that can advance workspace readiness.</summary>
public abstract record ReadinessSignal
{
    /// <summary>The LSP handshake (initialize/initialized) completed.</summary>
    public sealed record Initialized : ReadinessSignal;

    /// <summary>A <c>$/progress</c> "begin" with a "Loading" title arrived for <paramref name="Token"/>.</summary>
    public sealed record LoadingBegan(string Token) : ReadinessSignal;

    /// <summary>A <c>$/progress</c> "end" arrived for <paramref name="Token"/>.</summary>
    public sealed record ProgressEnded(string Token) : ReadinessSignal;

    /// <summary>The server reported <c>workspace/projectInitializationComplete</c>.</summary>
    public sealed record ProjectInitializationComplete : ReadinessSignal;

    /// <summary>The process-owned wall-clock fallback timer fired.</summary>
    public sealed record HardTimeoutElapsed : ReadinessSignal;
}

/// <summary>Result of <see cref="WorkspaceReadiness.Observe"/>: what happened, and frames to write outside the lock.</summary>
public readonly record struct ObserveResult(
    ReadinessTransition Transition,
    IReadOnlyList<byte[]> FramesToDrain);

/// <summary>
/// Pure decision module for the child-server readiness state machine (<c>Starting → Initialized → Ready</c>).
/// No I/O, no logger, no clock — callers translate frames/timers into <see cref="ReadinessSignal"/>s and
/// perform all writes themselves using the frames returned here.
/// </summary>
public sealed class WorkspaceReadiness
{
    private readonly object _lock = new();
    private ServerReadiness _state = ServerReadiness.Starting;
    private readonly List<byte[]> _pendingNotifications = new();
    private readonly List<byte[]> _pendingRequests = new();
    private readonly HashSet<string> _loadingTokens = new();
    private bool _seenLoadingToken;

    public ServerReadiness State { get { lock (_lock) return _state; } }

    /// <summary>
    /// Decides whether an outbound frame may be sent now, given its <paramref name="kind"/> and the
    /// current state. A queued frame is stashed and returned later by <see cref="Observe"/>.
    /// </summary>
    public GateDecision Gate(byte[] frame, FrameKind kind)
    {
        lock (_lock)
        {
            var requiredState = kind == FrameKind.Request ? ServerReadiness.Ready : ServerReadiness.Initialized;
            if (_state >= requiredState)
                return GateDecision.SendNow;

            (kind == FrameKind.Request ? _pendingRequests : _pendingNotifications).Add(frame);
            return GateDecision.Queued;
        }
    }

    /// <summary>Applies a readiness signal, returning the resulting transition and any frames it unblocked.</summary>
    public ObserveResult Observe(ReadinessSignal signal)
    {
        lock (_lock)
        {
            return signal switch
            {
                ReadinessSignal.Initialized => ObserveInitialized(),
                ReadinessSignal.LoadingBegan loadingBegan => ObserveLoadingBegan(loadingBegan),
                ReadinessSignal.ProgressEnded progressEnded => ObserveProgressEnded(progressEnded),
                ReadinessSignal.ProjectInitializationComplete or ReadinessSignal.HardTimeoutElapsed => TryBecomeReady(),
                _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
            };
        }
    }

    private ObserveResult ObserveInitialized()
    {
        if (_state != ServerReadiness.Starting)
            return new ObserveResult(ReadinessTransition.None, Array.Empty<byte[]>());

        _state = ServerReadiness.Initialized;
        var drained = _pendingNotifications.ToArray();
        _pendingNotifications.Clear();
        return new ObserveResult(ReadinessTransition.BecameInitialized, drained);
    }

    private ObserveResult ObserveLoadingBegan(ReadinessSignal.LoadingBegan signal)
    {
        _loadingTokens.Add(signal.Token);
        _seenLoadingToken = true;
        return new ObserveResult(ReadinessTransition.None, Array.Empty<byte[]>());
    }

    private ObserveResult ObserveProgressEnded(ReadinessSignal.ProgressEnded signal)
    {
        _loadingTokens.Remove(signal.Token);
        if (_loadingTokens.Count == 0 && _seenLoadingToken)
            return TryBecomeReady();
        return new ObserveResult(ReadinessTransition.None, Array.Empty<byte[]>());
    }

    private ObserveResult TryBecomeReady()
    {
        if (_state != ServerReadiness.Initialized)
            return new ObserveResult(ReadinessTransition.None, Array.Empty<byte[]>());

        _state = ServerReadiness.Ready;
        var drained = _pendingRequests.ToArray();
        _pendingRequests.Clear();
        return new ObserveResult(ReadinessTransition.BecameReady, drained);
    }
}
