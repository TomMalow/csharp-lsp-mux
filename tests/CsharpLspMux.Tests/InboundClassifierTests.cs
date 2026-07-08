using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class InboundClassifierTests
{
    private static Frame MakeFrame(JsonObject obj) => Frame.FromJson(obj);

    private static JsonObject MakeInitResponse() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 0,
        ["result"] = new JsonObject { ["capabilities"] = new JsonObject() }
    };

    private static JsonObject MakeResponse(string id) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id
    };

    private static JsonObject MakeNotification(string method) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method
    };

    private static JsonObject MakeWorkDoneProgressCreate(int id, string token) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "window/workDoneProgress/create",
        ["params"] = new JsonObject { ["token"] = token }
    };

    private static JsonObject MakeWorkspaceConfiguration(int id) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "workspace/configuration",
        ["params"] = new JsonObject { ["items"] = new JsonArray() }
    };

    private static JsonObject MakeProgressBegin(string token, string title) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "$/progress",
        ["params"] = new JsonObject
        {
            ["token"] = token,
            ["value"] = new JsonObject { ["kind"] = "begin", ["title"] = title }
        }
    };

    private static JsonObject MakeProgressEnd(string token) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "$/progress",
        ["params"] = new JsonObject
        {
            ["token"] = token,
            ["value"] = new JsonObject { ["kind"] = "end" }
        }
    };

    private static JsonObject MakeProgressReport(string token) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "$/progress",
        ["params"] = new JsonObject
        {
            ["token"] = token,
            ["value"] = new JsonObject { ["kind"] = "report" }
        }
    };

    private static InboundContext Ctx(bool awaitingInitialize = false, params string[] pendingIds)
        => new(new HashSet<string>(pendingIds), awaitingInitialize);

    [Fact]
    public void IdZeroResult_WhileAwaitingInitialize_IsInitializedSignal()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeInitResponse()), Ctx(awaitingInitialize: true));

        var signal = Assert.IsType<InboundAction.Signal>(action);
        Assert.IsType<ReadinessSignal.Initialized>(signal.Value);
    }

    [Fact]
    public void IdZeroResult_NotAwaitingInitialize_IsRelayToClient()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeInitResponse()), Ctx(awaitingInitialize: false));

        Assert.IsType<InboundAction.RelayToClient>(action);
    }

    [Fact]
    public void BareResponse_IdInPendingCorrelationIds_IsResolveCorrelation()
    {
        // Pending ids are stored in their JSON-quoted string form (JsonNode.ToJsonString()),
        // matching how RoslynServerProcess keys _pending.
        var action = InboundClassifier.Classify(MakeFrame(MakeResponse("__mux_1")), Ctx(pendingIds: "\"__mux_1\""));

        Assert.IsType<InboundAction.ResolveCorrelation>(action);
    }

    [Fact]
    public void BareResponse_IdNotPending_IsRelayToClient()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeResponse("99")), Ctx(pendingIds: "\"__mux_1\""));

        Assert.IsType<InboundAction.RelayToClient>(action);
    }

    [Fact]
    public void WorkDoneProgressCreate_IsRespondToChild_WithNullResultAndEchoedId()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeWorkDoneProgressCreate(10, "token1")), Ctx());

        var respond = Assert.IsType<InboundAction.RespondToChild>(action);
        Assert.Equal(10, respond.Response.Id?.GetValue<int>());
        Assert.Null(respond.Response.Json["result"]);
        Assert.True(respond.Response.Json.ContainsKey("result"));
    }

    [Fact]
    public void WorkspaceConfiguration_IsRespondToChild_WithEmptySettingsArrayAndEchoedId()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeWorkspaceConfiguration(99)), Ctx());

        var respond = Assert.IsType<InboundAction.RespondToChild>(action);
        Assert.Equal(99, respond.Response.Id?.GetValue<int>());
        var result = Assert.IsType<JsonArray>(respond.Response.Json["result"]);
        Assert.Single(result);
        Assert.Empty(((JsonObject)result[0]!));
    }

    [Fact]
    public void ProjectInitializationComplete_IsReadinessSignal()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeNotification("workspace/projectInitializationComplete")), Ctx());

        var signal = Assert.IsType<InboundAction.Signal>(action);
        Assert.IsType<ReadinessSignal.ProjectInitializationComplete>(signal.Value);
    }

    [Fact]
    public void ProgressBegin_WithLoadingTitle_IsDrop()
    {
        // $/progress carries no readiness meaning (ADR-0007) — even a "Loading" title is swallowed.
        var action = InboundClassifier.Classify(MakeFrame(MakeProgressBegin("token1", "Loading workspace...")), Ctx());

        Assert.IsType<InboundAction.Drop>(action);
    }

    [Fact]
    public void ProgressBegin_NonLoadingTitle_IsDrop()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeProgressBegin("tokenX", "Analyzing code")), Ctx());

        Assert.IsType<InboundAction.Drop>(action);
    }

    [Fact]
    public void ProgressEnd_IsDrop()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeProgressEnd("token1")), Ctx());

        Assert.IsType<InboundAction.Drop>(action);
    }

    [Fact]
    public void ProgressReport_IsDrop()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeProgressReport("token1")), Ctx());

        Assert.IsType<InboundAction.Drop>(action);
    }

    [Fact]
    public void Progress_MissingTokenOrKind_IsDrop()
    {
        var malformed = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "$/progress",
            ["params"] = new JsonObject()
        };

        var action = InboundClassifier.Classify(MakeFrame(malformed), Ctx());

        Assert.IsType<InboundAction.Drop>(action);
    }

    [Fact]
    public void RandomNotification_IsRelayToClient()
    {
        var action = InboundClassifier.Classify(MakeFrame(MakeNotification("textDocument/publishDiagnostics")), Ctx());

        Assert.IsType<InboundAction.RelayToClient>(action);
    }

    [Fact]
    public void FrameWithNeitherIdNorMethod_IsDrop()
    {
        var malformed = new JsonObject { ["jsonrpc"] = "2.0" };

        var action = InboundClassifier.Classify(MakeFrame(malformed), Ctx());

        Assert.IsType<InboundAction.Drop>(action);
    }
}
