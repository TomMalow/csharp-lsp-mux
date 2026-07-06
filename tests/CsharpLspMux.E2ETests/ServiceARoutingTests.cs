using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

public sealed class ServiceARoutingTests : IDisposable
{
    private static readonly string[] ClassFilePath = ["src", "ServiceA", "ServiceA.Api", "Class1.cs"];
    private static readonly string[] ConsumerFilePath = ["src", "ServiceA", "ServiceA.Consumer", "Class1.cs"];

    // ServiceAClient class declaration
    private const int ServiceAClientLine = 2;
    private const int ServiceAClientChar = 13;

    // ServiceAConsumer.Report() call site: client.GetStatus(1)
    private const int GetStatusCallLine = 9;
    private const int GetStatusCallChar = 22;

    private readonly MonoRepoFixture _fixture;

    public ServiceARoutingTests()
    {
        _fixture = new MonoRepoFixture();
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task ServiceA_HoverAndDefinition_ReturnsServiceASymbols()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = cts.Token;

        var psi = new ProcessStartInfo("dotnet", MuxPaths.MuxDll)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["REPO_ROOT"] = _fixture.TempDir }
        };

        using var process = Process.Start(psi)!;
        try
        {
            using var client = new LspClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);

            // 1. initialize
            var initResponse = await client.SendRequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = new Uri(_fixture.TempDir).AbsoluteUri,
                ["capabilities"] = new JsonObject()
            }, ct);

            Assert.NotNull(initResponse);
            Assert.Null(initResponse["error"]);

            // 2. initialized notification
            await client.SendNotificationAsync("initialized", new JsonObject(), ct);

            // 3. workspace/didChangeConfiguration
            await client.SendNotificationAsync("workspace/didChangeConfiguration", new JsonObject
            {
                ["settings"] = new JsonObject()
            }, ct);

            // 4. textDocument/didOpen for ServiceA Class1.cs
            var fileUri = new Uri(Path.Combine(_fixture.TempDir, "src", "ServiceA", "ServiceA.Api", "Class1.cs")).AbsoluteUri;
            await client.SendNotificationAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = fileUri,
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = _fixture.ReadFile(ClassFilePath)
                }
            }, ct);

            // 4b. textDocument/didOpen for the consumer, so hover can resolve the call site
            // through its ProjectReference to the library
            var consumerUri = new Uri(Path.Combine(_fixture.TempDir, "src", "ServiceA", "ServiceA.Consumer", "Class1.cs")).AbsoluteUri;
            await client.SendNotificationAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = consumerUri,
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = _fixture.ReadFile(ConsumerFilePath)
                }
            }, ct);

            // 5. textDocument/hover at the consumer's call site (client.GetStatus(1)) — proves
            // the consumer resolves the library symbol across the project reference and that
            // hover surfaces the resolved signature and XML doc summary
            var hoverResponse = await client.SendRequestAsync("textDocument/hover", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = consumerUri },
                ["position"] = new JsonObject { ["line"] = GetStatusCallLine, ["character"] = GetStatusCallChar }
            }, ct);

            Assert.NotNull(hoverResponse);
            Assert.Null(hoverResponse["error"]);
            Assert.NotNull(hoverResponse["result"]);
            var hoverText = hoverResponse["result"]!.ToJsonString();
            Assert.Contains("string ServiceAClient.GetStatus", hoverText);
            Assert.Contains("SENTINEL_SERVICE_A_GETSTATUS_DOC", hoverText);

            // 6. textDocument/definition at same position
            var definitionResponse = await client.SendRequestAsync("textDocument/definition", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                ["position"] = new JsonObject { ["line"] = ServiceAClientLine, ["character"] = ServiceAClientChar }
            }, ct);

            Assert.NotNull(definitionResponse);
            Assert.Null(definitionResponse["error"]);
            Assert.NotNull(definitionResponse["result"]);
            Assert.Contains("/src/ServiceA/", definitionResponse["result"]!.ToJsonString());

            // 7. textDocument/references on the ServiceAClient class declaration — proves
            // cross-project reference resolution: the library declaration plus the
            // `new ServiceAClient()` usage in the consumer project of the same solution.
            // No exact-count assertion, since whether Roslyn includes the declaration among
            // results is version-sensitive.
            var referencesResponse = await client.SendRequestAsync("textDocument/references", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                ["position"] = new JsonObject { ["line"] = ServiceAClientLine, ["character"] = ServiceAClientChar },
                ["context"] = new JsonObject { ["includeDeclaration"] = true }
            }, ct);

            Assert.NotNull(referencesResponse);
            Assert.Null(referencesResponse["error"]);
            Assert.NotNull(referencesResponse["result"]);
            var referencesText = referencesResponse["result"]!.ToJsonString();
            Assert.Contains("/ServiceA.Api/", referencesText);
            Assert.Contains("/ServiceA.Consumer/", referencesText);

            // Routing isolation: a ServiceA-scoped request must never surface ServiceB
            // content — catches a routing bug that broadcasts a scoped request to all servers.
            Assert.DoesNotContain("ServiceB", hoverText);
            Assert.DoesNotContain("ServiceB", definitionResponse["result"]!.ToJsonString());
            Assert.DoesNotContain("ServiceB", referencesText);

            // 8. shutdown
            var shutdownResponse = await client.SendRequestAsync("shutdown", null, ct);
            Assert.NotNull(shutdownResponse);
            Assert.Null(shutdownResponse["error"]);

            // 8. exit
            await client.SendNotificationAsync("exit", null, ct);

            await process.WaitForExitAsync(ct);
            Assert.Equal(0, process.ExitCode);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    public void Dispose() => _fixture.Dispose();
}
