using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class WorkspaceReadinessTests
{
    private static Frame Req(string tag) => Frame.FromJson(new JsonObject { ["id"] = tag, ["method"] = tag });

    private static Frame Notif(string tag) => Frame.FromJson(new JsonObject { ["method"] = tag });

    private static string Tag(Frame frame) => frame.Method!;

    // --- Gate matrix ---

    [Fact]
    public void Gate_Starting_Request_IsQueued()
    {
        var readiness = new WorkspaceReadiness();

        var decision = readiness.Gate(Req("r1"));

        Assert.Equal(GateDecision.Queued, decision);
    }

    [Fact]
    public void Gate_Starting_Notification_IsQueued()
    {
        var readiness = new WorkspaceReadiness();

        var decision = readiness.Gate(Notif("n1"));

        Assert.Equal(GateDecision.Queued, decision);
    }

    [Fact]
    public void Gate_Initialized_Request_IsQueued()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var decision = readiness.Gate(Req("r1"));

        Assert.Equal(GateDecision.Queued, decision);
    }

    [Fact]
    public void Gate_Initialized_Notification_IsSendNow()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());

        var decision = readiness.Gate(Notif("n1"));

        Assert.Equal(GateDecision.SendNow, decision);
    }

    [Fact]
    public void Gate_Ready_Request_IsSendNow()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var decision = readiness.Gate(Req("r1"));

        Assert.Equal(GateDecision.SendNow, decision);
    }

    [Fact]
    public void Gate_Ready_Notification_IsSendNow()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());

        var decision = readiness.Gate(Notif("n1"));

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
        readiness.Gate(Notif("n1"));
        readiness.Gate(Req("r1"));

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
        readiness.Gate(Notif("n1"));
        readiness.Gate(Notif("n2"));
        readiness.Gate(Notif("n3"));

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
        readiness.Gate(Req("r1"));
        readiness.Gate(Req("r2"));

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
        readiness.Gate(Req("r1"));

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

    // --- Gate does not leak frames between drains ---

    [Fact]
    public void Observe_BecameReady_ThenNewRequestQueuedAgainBeforeNextReadyCall_DoesNotRedrainOldFrames()
    {
        var readiness = new WorkspaceReadiness();
        readiness.Observe(new ReadinessSignal.Initialized());
        readiness.Gate(Req("r1"));
        var first = readiness.Observe(new ReadinessSignal.ProjectInitializationComplete());
        Assert.Equal(new[] { "r1" }, first.FramesToDrain.Select(Tag));

        var second = readiness.Observe(new ReadinessSignal.HardTimeoutElapsed());

        Assert.Equal(ReadinessTransition.None, second.Transition);
        Assert.Empty(second.FramesToDrain);
    }
}
