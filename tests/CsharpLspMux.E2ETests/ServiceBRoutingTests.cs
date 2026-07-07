using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

[Collection(MuxServerCollection.Name)]
public sealed class ServiceBRoutingTests(MuxServerFixture fixture)
{
    // ServiceBWorker class declaration
    private const int ServiceBWorkerLine = 2;
    private const int ServiceBWorkerChar = 13;

    // ServiceBConsumer.Run() call site: worker.Process(1)
    private const int ProcessCallLine = 9;
    private const int ProcessCallChar = 22;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Hover_AtConsumerCallSite_ResolvesLibrarySignatureAndDoc()
    {
        using var cts = new CancellationTokenSource(Timeout);

        // Hover at the consumer's call site (worker.Process(1)) — proves the consumer resolves
        // the library symbol across the project reference and that hover surfaces the resolved
        // signature and XML doc summary.
        var hoverResponse = await fixture.Client.SendRequestAsync("textDocument/hover", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.ServiceBConsumerUri },
            ["position"] = new JsonObject { ["line"] = ProcessCallLine, ["character"] = ProcessCallChar }
        }, cts.Token);

        Assert.NotNull(hoverResponse);
        Assert.Null(hoverResponse["error"]);
        Assert.NotNull(hoverResponse["result"]);
        var hoverText = hoverResponse["result"]!.ToJsonString();
        Assert.Contains("string ServiceBWorker.Process", hoverText);
        Assert.Contains("SENTINEL_SERVICE_B_PROCESS_DOC", hoverText);

        // Routing isolation: a ServiceB-scoped request must never surface ServiceA content —
        // catches a routing bug that broadcasts a scoped request to all servers.
        Assert.DoesNotContain("ServiceA", hoverText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Definition_OnClassDeclaration_ReturnsServiceBSymbol()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var definitionResponse = await fixture.Client.SendRequestAsync("textDocument/definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.ServiceBWorkerUri },
            ["position"] = new JsonObject { ["line"] = ServiceBWorkerLine, ["character"] = ServiceBWorkerChar }
        }, cts.Token);

        Assert.NotNull(definitionResponse);
        Assert.Null(definitionResponse["error"]);
        Assert.NotNull(definitionResponse["result"]);
        var definitionText = definitionResponse["result"]!.ToJsonString();
        Assert.Contains("/src/ServiceB/", definitionText);
        Assert.DoesNotContain("ServiceA", definitionText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task References_OnClassDeclaration_ReturnsDeclarationAndConsumerUsage()
    {
        using var cts = new CancellationTokenSource(Timeout);

        // References on the ServiceBWorker class declaration — proves cross-project reference
        // resolution: the library declaration plus the `new ServiceBWorker()` usage in the
        // consumer project of the same solution. No exact-count assertion, since whether Roslyn
        // includes the declaration among results is version-sensitive.
        var referencesResponse = await fixture.Client.SendRequestAsync("textDocument/references", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.ServiceBWorkerUri },
            ["position"] = new JsonObject { ["line"] = ServiceBWorkerLine, ["character"] = ServiceBWorkerChar },
            ["context"] = new JsonObject { ["includeDeclaration"] = true }
        }, cts.Token);

        Assert.NotNull(referencesResponse);
        Assert.Null(referencesResponse["error"]);
        Assert.NotNull(referencesResponse["result"]);
        var referencesText = referencesResponse["result"]!.ToJsonString();
        Assert.Contains("/ServiceB.Worker/", referencesText);
        Assert.Contains("/ServiceB.Consumer/", referencesText);
        Assert.DoesNotContain("ServiceA", referencesText);
    }
}
