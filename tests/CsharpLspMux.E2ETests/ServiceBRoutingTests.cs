using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

public sealed class ServiceBRoutingTests : IDisposable
{
    private static readonly string[] ServiceAClassFilePath = ["src", "ServiceA", "ServiceA.Api", "Class1.cs"];
    private static readonly string[] ServiceBClassFilePath = ["src", "ServiceB", "ServiceB.Worker", "Class1.cs"];
    private static readonly string[] ServiceBConsumerFilePath = ["src", "ServiceB", "ServiceB.Consumer", "Class1.cs"];

    // ServiceAClient / ServiceBWorker class declarations
    private const int ClassDeclarationLine = 2;
    private const int ClassDeclarationChar = 13;

    // ServiceBConsumer.Run() call site: worker.Process(1)
    private const int ProcessCallLine = 9;
    private const int ProcessCallChar = 22;

    private readonly MonoRepoFixture _fixture;

    public ServiceBRoutingTests()
    {
        _fixture = new MonoRepoFixture();
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task ServiceB_HoverAndDefinition_ReturnsServiceBSymbols()
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

            var initResponse = await client.SendRequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = new Uri(_fixture.TempDir).AbsoluteUri,
                ["capabilities"] = new JsonObject()
            }, ct);

            Assert.NotNull(initResponse);
            Assert.Null(initResponse["error"]);

            await client.SendNotificationAsync("initialized", new JsonObject(), ct);
            await client.SendNotificationAsync("workspace/didChangeConfiguration", new JsonObject
            {
                ["settings"] = new JsonObject()
            }, ct);

            var fileUri = new Uri(Path.Combine(_fixture.TempDir, "src", "ServiceB", "ServiceB.Worker", "Class1.cs")).AbsoluteUri;
            await client.SendNotificationAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = fileUri,
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = _fixture.ReadFile(ServiceBClassFilePath)
                }
            }, ct);

            // textDocument/didOpen for the consumer, so hover can resolve the call site
            // through its ProjectReference to the library
            var consumerUri = new Uri(Path.Combine(_fixture.TempDir, "src", "ServiceB", "ServiceB.Consumer", "Class1.cs")).AbsoluteUri;
            await client.SendNotificationAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = consumerUri,
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = _fixture.ReadFile(ServiceBConsumerFilePath)
                }
            }, ct);

            // textDocument/hover at the consumer's call site (worker.Process(1)) — proves
            // the consumer resolves the library symbol across the project reference and that
            // hover surfaces the resolved signature and XML doc summary
            var hoverResponse = await client.SendRequestAsync("textDocument/hover", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = consumerUri },
                ["position"] = new JsonObject { ["line"] = ProcessCallLine, ["character"] = ProcessCallChar }
            }, ct);

            Assert.NotNull(hoverResponse);
            Assert.Null(hoverResponse["error"]);
            Assert.NotNull(hoverResponse["result"]);
            var hoverText = hoverResponse["result"]!.ToJsonString();
            Assert.Contains("string ServiceBWorker.Process", hoverText);
            Assert.Contains("SENTINEL_SERVICE_B_PROCESS_DOC", hoverText);

            var definitionResponse = await client.SendRequestAsync("textDocument/definition", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                ["position"] = new JsonObject { ["line"] = ClassDeclarationLine, ["character"] = ClassDeclarationChar }
            }, ct);

            Assert.NotNull(definitionResponse);
            Assert.Null(definitionResponse["error"]);
            Assert.NotNull(definitionResponse["result"]);
            Assert.Contains("/src/ServiceB/", definitionResponse["result"]!.ToJsonString());

            // textDocument/references on the ServiceBWorker class declaration — proves
            // cross-project reference resolution: the library declaration plus the
            // `new ServiceBWorker()` usage in the consumer project of the same solution.
            // No exact-count assertion, since whether Roslyn includes the declaration among
            // results is version-sensitive.
            var referencesResponse = await client.SendRequestAsync("textDocument/references", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                ["position"] = new JsonObject { ["line"] = ClassDeclarationLine, ["character"] = ClassDeclarationChar },
                ["context"] = new JsonObject { ["includeDeclaration"] = true }
            }, ct);

            Assert.NotNull(referencesResponse);
            Assert.Null(referencesResponse["error"]);
            Assert.NotNull(referencesResponse["result"]);
            var referencesText = referencesResponse["result"]!.ToJsonString();
            Assert.Contains("/ServiceB.Worker/", referencesText);
            Assert.Contains("/ServiceB.Consumer/", referencesText);

            // Routing isolation: a ServiceB-scoped request must never surface ServiceA
            // content — catches a routing bug that broadcasts a scoped request to all servers.
            Assert.DoesNotContain("ServiceA", hoverText);
            Assert.DoesNotContain("ServiceA", definitionResponse["result"]!.ToJsonString());
            Assert.DoesNotContain("ServiceA", referencesText);

            var shutdownResponse = await client.SendRequestAsync("shutdown", null, ct);
            Assert.NotNull(shutdownResponse);
            Assert.Null(shutdownResponse["error"]);

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

    [Fact]
    [Trait("Category", "E2E")]
    public async Task WorkspaceSymbol_BothServersActive_ReturnsBothSymbols()
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

            var initResponse = await client.SendRequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = new Uri(_fixture.TempDir).AbsoluteUri,
                ["capabilities"] = new JsonObject()
            }, ct);

            Assert.NotNull(initResponse);
            Assert.Null(initResponse["error"]);

            await client.SendNotificationAsync("initialized", new JsonObject(), ct);
            await client.SendNotificationAsync("workspace/didChangeConfiguration", new JsonObject
            {
                ["settings"] = new JsonObject()
            }, ct);

            // Open ServiceA — triggers ServiceA Roslyn server
            var serviceAUri = new Uri(Path.Combine(_fixture.TempDir, "src", "ServiceA", "ServiceA.Api", "Class1.cs")).AbsoluteUri;
            await client.SendNotificationAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = serviceAUri,
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = _fixture.ReadFile(ServiceAClassFilePath)
                }
            }, ct);

            // Open ServiceB — triggers ServiceB Roslyn server
            var serviceBUri = new Uri(Path.Combine(_fixture.TempDir, "src", "ServiceB", "ServiceB.Worker", "Class1.cs")).AbsoluteUri;
            await client.SendNotificationAsync("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = serviceBUri,
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = _fixture.ReadFile(ServiceBClassFilePath)
                }
            }, ct);

            // Ensures InitBarrier has fired on both Roslyn servers before workspace/symbol broadcast
            var hoverA = await client.SendRequestAsync("textDocument/hover", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = serviceAUri },
                ["position"] = new JsonObject { ["line"] = ClassDeclarationLine, ["character"] = ClassDeclarationChar }
            }, ct);
            Assert.NotNull(hoverA);
            Assert.Null(hoverA["error"]);
            Assert.NotNull(hoverA["result"]);
            Assert.Contains("ServiceAClient", hoverA["result"]!.ToJsonString());

            var hoverB = await client.SendRequestAsync("textDocument/hover", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = serviceBUri },
                ["position"] = new JsonObject { ["line"] = ClassDeclarationLine, ["character"] = ClassDeclarationChar }
            }, ct);
            Assert.NotNull(hoverB);
            Assert.Null(hoverB["error"]);
            Assert.NotNull(hoverB["result"]);
            Assert.Contains("ServiceBWorker", hoverB["result"]!.ToJsonString());

            // workspace/symbol broadcast — both servers must respond and results merged.
            // A non-empty query is required: Roslyn returns [] for an empty query.
            var symbolResponse = await client.SendRequestAsync("workspace/symbol", new JsonObject
            {
                ["query"] = "Service"
            }, ct);

            Assert.NotNull(symbolResponse);
            Assert.Null(symbolResponse["error"]);
            Assert.NotNull(symbolResponse["result"]);

            // Proves both the broadcast to all active servers and the merge of their result
            // arrays: the merged result contains a named symbol from each solution.
            var symbolText = symbolResponse["result"]!.ToJsonString();
            Assert.Contains("ServiceAClient", symbolText);
            Assert.Contains("ServiceBWorker", symbolText);

            var shutdownResponse = await client.SendRequestAsync("shutdown", null, ct);
            Assert.NotNull(shutdownResponse);
            Assert.Null(shutdownResponse["error"]);

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
