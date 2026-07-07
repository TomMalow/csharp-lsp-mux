using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

[Collection(MuxServerCollection.Name)]
public sealed class WorkspaceSymbolTests(MuxServerFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task BothServersActive_ReturnsBothSolutionsSymbolsMerged()
    {
        using var cts = new CancellationTokenSource(Timeout);

        // workspace/symbol broadcast — both servers must respond and results merged. A
        // non-empty query is required: Roslyn returns [] for an empty query.
        var symbolResponse = await fixture.Client.SendRequestAsync("workspace/symbol", new JsonObject
        {
            ["query"] = "Service"
        }, cts.Token);

        Assert.NotNull(symbolResponse);
        Assert.Null(symbolResponse["error"]);
        Assert.NotNull(symbolResponse["result"]);

        // Proves both the broadcast to all active servers and the merge of their result arrays:
        // the merged result contains a named symbol from each solution.
        var symbolText = symbolResponse["result"]!.ToJsonString();
        Assert.Contains("ServiceAClient", symbolText);
        Assert.Contains("ServiceBWorker", symbolText);
    }
}
