using System.Text.Json.Nodes;

namespace CsharpLspMux;

/// <summary>Facts the classifier needs but does not own — correlation and the init handshake stay in <see cref="RoslynServerProcess"/>.</summary>
public readonly record struct InboundContext(
    IReadOnlySet<string> PendingCorrelationIds,
    bool AwaitingInitialize);

/// <summary>What to do with one inbound (server→client) frame.</summary>
public abstract record InboundAction
{
    /// <summary>Forward the frame to the client unchanged.</summary>
    public sealed record RelayToClient : InboundAction;

    /// <summary>Write <see cref="Response"/> back to the child server; never reaches the client.</summary>
    public sealed record RespondToChild(Frame Response) : InboundAction;

    /// <summary>Complete the pending <c>SendAndReceiveAsync</c> call waiting on this frame's id.</summary>
    public sealed record ResolveCorrelation : InboundAction;

    /// <summary>Feed <paramref name="Value"/> into the server's <see cref="WorkspaceReadiness"/>.</summary>
    public sealed record Signal(ReadinessSignal Value) : InboundAction;

    /// <summary>Neither relayed nor acted on.</summary>
    public sealed record Drop : InboundAction;
}

/// <summary>
/// Pure decision module mirroring <see cref="MuxDispatcher"/> for the server→client direction:
/// classifies one inbound frame into an <see cref="InboundAction"/>. No I/O, no state — the read
/// loop executes the returned action.
/// </summary>
public static class InboundClassifier
{
    public static InboundAction Classify(Frame frame, InboundContext ctx)
    {
        var method = frame.Method;

        if (method is null && frame.Id is null)
            return new InboundAction.Drop(); // malformed: neither a response nor a notification

        if (method is null && ctx.AwaitingInitialize && frame.Id?.ToJsonString() == "0" && frame.Json["result"] is not null)
            return new InboundAction.Signal(new ReadinessSignal.Initialized());

        if (method is null && frame.Id is JsonNode idNode && ctx.PendingCorrelationIds.Contains(idNode.ToJsonString()))
            return new InboundAction.ResolveCorrelation();

        if (method == "window/workDoneProgress/create" && frame.Id is JsonNode progressCreateId)
            return new InboundAction.RespondToChild(Frame.FromJson(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = progressCreateId.DeepClone(),
                ["result"] = JsonValue.Create<object?>(null)
            }));

        if (method == "workspace/configuration" && frame.Id is JsonNode configId)
            return new InboundAction.RespondToChild(Frame.FromJson(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = configId.DeepClone(),
                ["result"] = new JsonArray(new JsonObject())
            }));

        if (method == "workspace/projectInitializationComplete")
            return new InboundAction.Signal(new ReadinessSignal.ProjectInitializationComplete());

        // $/progress carries no readiness meaning (see ADR-0007): Roslyn never emits it in
        // practice, and readiness rests solely on projectInitializationComplete + the hard
        // timeout. Swallowed rather than relayed, matching the other server-internal notifications above.
        if (method == "$/progress")
            return new InboundAction.Drop();

        return new InboundAction.RelayToClient();
    }
}
