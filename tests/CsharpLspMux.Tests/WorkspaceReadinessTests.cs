using System.Text;
using Xunit;

namespace CsharpLspMux.Tests;

public class WorkspaceReadinessTests
{
    private static byte[] Frame(string tag) => Encoding.UTF8.GetBytes(tag);

    private static string Tag(byte[] frame) => Encoding.UTF8.GetString(frame);

    // --- Gate matrix ---

    [Fact]
    public void Gate_Starting_Request_IsQueued()
    {
        var readiness = new WorkspaceReadiness();

        var decision = readiness.Gate(Frame("r1"), FrameKind.Request);

        Assert.Equal(GateDecision.Queued, decision);
    }

    [Fact]
    public void Gate_Starting_Notification_IsQueued()
    {
        var readiness = new WorkspaceReadiness();

        var decision = readiness.Gate(Frame("n1"), FrameKind.Notification);

        Assert.Equal(GateDecision.Queued, decision);
    }

    [Fact]
    public void Gate_Initialized_Request_IsQueued()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var decision = readiness.Gate(Frame("r1"), FrameKind.Request);

        Assert.Equal(GateDecision.Queued, decision);
    }

    [Fact]
    public void Gate_Initialized_Notification_IsSendNow()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var decision = readiness.Gate(Frame("n1"), FrameKind.Notification);

        Assert.Equal(GateDecision.SendNow, decision);
    }

    [Fact]
    public void Gate_Ready_Request_IsSendNow()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var decision = readiness.Gate(Frame("r1"), FrameKind.Request);

        Assert.Equal(GateDecision.SendNow, decision);
    }

    [Fact]
    public void Gate_Ready_Notification_IsSendNow()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var decision = readiness.Gate(Frame("n1"), FrameKind.Notification);

        Assert.Equal(GateDecision.SendNow, decision);
    }

    // --- State reflects transitions ---

    [Fact]
    public void State_Initially_IsStarting()
    {
        var readiness = new WorkspaceReadiness();

        Assert.Equal(ServerReadiness.Starting, readiness.State);
    }

    [Fact]
    public void State_AfterInitialized_IsInitialized()
    {
        var readiness = new WorkspaceReadiness();

        readiness.Observe(new ReadinessSignal.Initialized());

        Assert.Equal(ServerReadiness.Initialized, readiness.State);
    }

    [Fact]
    public void State_AfterProjectInitializationComplete_IsReady()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        Assert.Equal(ServerReadiness.Ready, readiness.State);
    }

    // --- Initialized transition ---

    [Fact]
    public void Observe_Initialized_FromStarting_ReturnsBecameInitialized()
    {
        var readiness = new WorkspaceReadiness();

        var result = readiness.Observe(new ReadinessSignal.Initialized());

        Assert.Equal(ReadinessTransition.BecameInitialized, result.Transition);
    }

    [Fact]
    public void Observe_Initialized_DrainsQueuedNotifications_NotRequests()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Gate(Frame("n1"), FrameKind.Notification);
        readiness.Gate(Frame("r1"), FrameKind.Request);

        var result = readiness.Observe(new ReadinessSignal.Initialized());

        Assert.Equal(new[] { "n1" }, result.FramesToDrain.Select(Tag));
    }

    [Fact]
    public void Observe_Initialized_WithNoQueuedNotifications_DrainsEmpty()
    {
        var readiness = new WorkspaceReadiness();

        var result = readiness.Observe(new ReadinessSignal.Initialized());

        Assert.Empty(result.FramesToDrain);
    }

    [Fact]
    public void Observe_Initialized_DrainsNotificationsInFifoOrder()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Gate(Frame("n1"), FrameKind.Notification);
        readiness.Gate(Frame("n2"), FrameKind.Notification);
        readiness.Gate(Frame("n3"), FrameKind.Notification);

        var result = readiness.Observe(new ReadinessSignal.Initialized());

        Assert.Equal(new[] { "n1", "n2", "n3" }, result.FramesToDrain.Select(Tag));
    }

    [Fact]
    public void Observe_Initialized_WhileAlreadyInitialized_ReturnsNone()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var result = readiness.Observe(new ReadinessSignal.Initialized());

        Assert.Equal(ReadinessTransition.None, result.Transition);
        Assert.Empty(result.FramesToDrain);
    }

    [Fact]
    public void Observe_Initialized_WhileReady_ReturnsNone()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var result = readiness.Observe(new ReadinessSignal.Initialized());

        Assert.Equal(ReadinessTransition.None, result.Transition);
    }

    // --- ProjectInitializationComplete / HardTimeoutElapsed transition to Ready ---

    [Fact]
    public void Observe_ProjectInitializationComplete_FromInitialized_ReturnsBecameReady()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var result = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        Assert.Equal(ReadinessTransition.BecameReady, result.Transition);
    }

    [Fact]
    public void Observe_ProjectInitializationComplete_DrainsQueuedRequests()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Gate(Frame("r1"), FrameKind.Request);
        readiness.Gate(Frame("r2"), FrameKind.Request);

        var result = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        Assert.Equal(new[] { "r1", "r2" }, result.FramesToDrain.Select(Tag));
    }

    [Fact]
    public void Observe_ProjectInitializationComplete_WithNoQueuedRequests_DrainsEmpty()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var result = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        Assert.Empty(result.FramesToDrain);
    }

    [Fact]
    public void Observe_ProjectInitializationComplete_WhileStarting_ReturnsNone()
    {
        var readiness = new WorkspaceReadiness();

        var result = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        Assert.Equal(ReadinessTransition.None, result.Transition);
        Assert.Equal(ServerReadiness.Starting, readiness.State);
    }

    [Fact]
    public void Observe_ProjectInitializationComplete_CalledTwice_SecondReturnsNone()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var result = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        Assert.Equal(ReadinessTransition.None, result.Transition);
        Assert.Empty(result.FramesToDrain);
    }

    [Fact]
    public void Observe_HardTimeoutElapsed_FromInitialized_ReturnsBecameReadyAndDrainsRequests()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Gate(Frame("r1"), FrameKind.Request);

        var result = readiness.Observe(new ReadinessSignal.HardTimeoutElapsed());

        Assert.Equal(ReadinessTransition.BecameReady, result.Transition);
        Assert.Equal(new[] { "r1" }, result.FramesToDrain.Select(Tag));
        Assert.Equal(ServerReadiness.Ready, readiness.State);
    }

    [Fact]
    public void Observe_HardTimeoutElapsed_AfterAlreadyReady_ReturnsNone()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var result = readiness.Observe(new ReadinessSignal.HardTimeoutElapsed());

        Assert.Equal(ReadinessTransition.None, result.Transition);
        Assert.Empty(result.FramesToDrain);
    }

    [Fact]
    public void Observe_ProjectInitializationComplete_AfterHardTimeoutAlreadyFired_ReturnsNone()
    {
        // Idempotency: whichever signal reaches Ready first wins, the racing one is a no-op.
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.HardTimeoutElapsed());

        var result = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        Assert.Equal(ReadinessTransition.None, result.Transition);
        Assert.Empty(result.FramesToDrain);
    }

    // --- Loading token set + latch ---

    [Fact]
    public void Observe_LoadingBegan_AlwaysReturnsNone()
    {
        var readiness = new WorkspaceReadiness();

        var result = readiness.Observe(new ReadinessSignal.LoadingBegan("t1"));

        Assert.Equal(ReadinessTransition.None, result.Transition);
    }

    [Fact]
    public void Observe_LoadingBegan_WhileStarting_MutatesRegardlessOfState()
    {
        // The latch takes effect even before Initialized, so a subsequent ProgressEnded honours it once ready.
        var readiness = new WorkspaceReadiness();

        readiness.Observe(new ReadinessSignal.LoadingBegan("t1"));
        readiness.Observe(new ReadinessSignal.Initialized());
        var result = readiness.Observe(new ReadinessSignal.ProgressEnded("t1"));

        Assert.Equal(ReadinessTransition.BecameReady, result.Transition);
    }

    [Fact]
    public void Observe_LoadingBegan_WithoutMatchingProgressEnded_HoldsInitialized()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        readiness.Observe(new ReadinessSignal.LoadingBegan("t1"));

        Assert.Equal(ServerReadiness.Initialized, readiness.State);
    }

    [Fact]
    public void Observe_ProgressEnded_WithActiveLoadingToken_WhileInitialized_BecomesReady()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.LoadingBegan("t1"));
        readiness.Gate(Frame("r1"), FrameKind.Request);

        var result = readiness.Observe(new ReadinessSignal.ProgressEnded("t1"));

        Assert.Equal(ReadinessTransition.BecameReady, result.Transition);
        Assert.Equal(new[] { "r1" }, result.FramesToDrain.Select(Tag));
    }

    [Fact]
    public void Observe_ProgressEnded_WithoutMatchingLoadingBegan_IsNoOp()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var result = readiness.Observe(new ReadinessSignal.ProgressEnded("unknown-token"));

        Assert.Equal(ReadinessTransition.None, result.Transition);
        Assert.Equal(ServerReadiness.Initialized, readiness.State);
    }

    [Fact]
    public void Observe_ProgressEnded_WithOtherTokensStillActive_GateHoldsOpen()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.LoadingBegan("tokenA"));
        readiness.Observe(new ReadinessSignal.LoadingBegan("tokenB"));

        var result = readiness.Observe(new ReadinessSignal.ProgressEnded("tokenA"));

        Assert.Equal(ReadinessTransition.None, result.Transition);
        Assert.Equal(ServerReadiness.Initialized, readiness.State);
    }

    [Fact]
    public void Observe_ProgressEnded_WhenLastTokenClears_BecomesReady()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.LoadingBegan("tokenA"));
        readiness.Observe(new ReadinessSignal.LoadingBegan("tokenB"));
        readiness.Observe(new ReadinessSignal.ProgressEnded("tokenA"));

        var result = readiness.Observe(new ReadinessSignal.ProgressEnded("tokenB"));

        Assert.Equal(ReadinessTransition.BecameReady, result.Transition);
    }

    [Fact]
    public void Observe_ProgressEnded_WithoutAnyLoadingBegan_NeverBlocksReady()
    {
        // _seenLoadingToken latch was never set — the loading heuristic never engaged, so
        // readiness transitions solely via other signals (e.g. hard timeout).
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var result = readiness.Observe(new ReadinessSignal.HardTimeoutElapsed());

        Assert.Equal(ReadinessTransition.BecameReady, result.Transition);
    }

    [Fact]
    public void Observe_ProgressEnded_AfterAlreadyReady_ReturnsNoneButStillClearsToken()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.LoadingBegan("t1"));
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var result = readiness.Observe(new ReadinessSignal.ProgressEnded("t1"));

        Assert.Equal(ReadinessTransition.None, result.Transition);
    }

    // --- Gate does not leak frames between drains ---

    [Fact]
    public void Observe_BecameReady_ThenNewRequestQueuedAgainBeforeNextReadyCall_DoesNotRedrainOldFrames()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Gate(Frame("r1"), FrameKind.Request);
        var first = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());
        Assert.Equal(new[] { "r1" }, first.FramesToDrain.Select(Tag));

        var second = readiness.Observe(new ReadinessSignal.HardTimeoutElapsed());

        Assert.Equal(ReadinessTransition.None, second.Transition);
        Assert.Empty(second.FramesToDrain);
    }
}
