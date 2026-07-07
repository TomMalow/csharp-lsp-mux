using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

[Collection(MuxServerCollection.Name)]
public sealed class ServiceARoutingTests(MuxServerFixture fixture)
{
    // ServiceAClient class declaration
    private const int ServiceAClientLine = 2;
    private const int ServiceAClientChar = 13;

    // ServiceAConsumer.Report() call site: client.GetStatus(1)
    private const int GetStatusCallLine = 9;
    private const int GetStatusCallChar = 22;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Hover_AtConsumerCallSite_ResolvesLibrarySignatureAndDoc()
    {
        using var cts = new CancellationTokenSource(Timeout);

        // Hover at the consumer's call site (client.GetStatus(1)) — proves the consumer resolves
        // the library symbol across the project reference and that hover surfaces the resolved
        // signature and XML doc summary.
        var hoverResponse = await fixture.Client.SendRequestAsync("textDocument/hover", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.ServiceAConsumerUri },
            ["position"] = new JsonObject { ["line"] = GetStatusCallLine, ["character"] = GetStatusCallChar }
        }, cts.Token);

        Assert.NotNull(hoverResponse);
        Assert.Null(hoverResponse["error"]);
        Assert.NotNull(hoverResponse["result"]);
        var hoverText = hoverResponse["result"]!.ToJsonString();
        Assert.Contains("string ServiceAClient.GetStatus", hoverText);
        Assert.Contains("SENTINEL_SERVICE_A_GETSTATUS_DOC", hoverText);

        // Routing isolation: a ServiceA-scoped request must never surface ServiceB content —
        // catches a routing bug that broadcasts a scoped request to all servers.
        Assert.DoesNotContain("ServiceB", hoverText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Definition_OnClassDeclaration_ReturnsServiceASymbol()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var definitionResponse = await fixture.Client.SendRequestAsync("textDocument/definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.ServiceAApiUri },
            ["position"] = new JsonObject { ["line"] = ServiceAClientLine, ["character"] = ServiceAClientChar }
        }, cts.Token);

        Assert.NotNull(definitionResponse);
        Assert.Null(definitionResponse["error"]);
        Assert.NotNull(definitionResponse["result"]);
        var definitionText = definitionResponse["result"]!.ToJsonString();
        Assert.Contains("/src/ServiceA/", definitionText);
        Assert.DoesNotContain("ServiceB", definitionText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task References_OnClassDeclaration_ReturnsDeclarationAndConsumerUsage()
    {
        using var cts = new CancellationTokenSource(Timeout);

        // References on the ServiceAClient class declaration — proves cross-project reference
        // resolution: the library declaration plus the `new ServiceAClient()` usage in the
        // consumer project of the same solution. No exact-count assertion, since whether Roslyn
        // includes the declaration among results is version-sensitive.
        var referencesResponse = await fixture.Client.SendRequestAsync("textDocument/references", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.ServiceAApiUri },
            ["position"] = new JsonObject { ["line"] = ServiceAClientLine, ["character"] = ServiceAClientChar },
            ["context"] = new JsonObject { ["includeDeclaration"] = true }
        }, cts.Token);

        Assert.NotNull(referencesResponse);
        Assert.Null(referencesResponse["error"]);
        Assert.NotNull(referencesResponse["result"]);
        var referencesText = referencesResponse["result"]!.ToJsonString();
        Assert.Contains("/ServiceA.Api/", referencesText);
        Assert.Contains("/ServiceA.Consumer/", referencesText);
        Assert.DoesNotContain("ServiceB", referencesText);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task DocumentSymbol_OnApiFile_ReturnsFileSymbolsScopedToServiceA()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var documentSymbolResponse = await fixture.Client.SendRequestAsync("textDocument/documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.ServiceAApiUri }
        }, cts.Token);

        Assert.NotNull(documentSymbolResponse);
        Assert.Null(documentSymbolResponse["error"]);
        Assert.NotNull(documentSymbolResponse["result"]);
        var symbolText = documentSymbolResponse["result"]!.ToJsonString();
        Assert.Contains("ServiceAClient", symbolText);
        Assert.Contains("GetStatus", symbolText);
        Assert.DoesNotContain("ServiceB", symbolText);
    }
}
