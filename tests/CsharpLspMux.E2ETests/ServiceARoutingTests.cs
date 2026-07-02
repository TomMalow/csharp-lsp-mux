using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

public sealed class ServiceARoutingTests : IDisposable
{
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
                    ["text"] = "namespace ServiceA.Api;\n\npublic class ServiceAClient\n{\n}\n"
                }
            }, ct);

            // 5. textDocument/hover at ServiceAClient declaration (line 2, char 13)
            var hoverResponse = await client.SendRequestAsync("textDocument/hover", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                ["position"] = new JsonObject { ["line"] = 2, ["character"] = 13 }
            }, ct);

            Assert.NotNull(hoverResponse);
            Assert.Null(hoverResponse["error"]);
            Assert.NotNull(hoverResponse["result"]);
            Assert.Contains("ServiceAClient", hoverResponse["result"]!.ToJsonString());

            // 6. textDocument/definition at same position
            var definitionResponse = await client.SendRequestAsync("textDocument/definition", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                ["position"] = new JsonObject { ["line"] = 2, ["character"] = 13 }
            }, ct);

            Assert.NotNull(definitionResponse);
            Assert.Null(definitionResponse["error"]);
            Assert.NotNull(definitionResponse["result"]);
            Assert.Contains("/src/ServiceA/", definitionResponse["result"]!.ToJsonString());

            // 7. textDocument/references at same position
            var referencesResponse = await client.SendRequestAsync("textDocument/references", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = fileUri },
                ["position"] = new JsonObject { ["line"] = 2, ["character"] = 13 },
                ["context"] = new JsonObject { ["includeDeclaration"] = true }
            }, ct);

            Assert.NotNull(referencesResponse);
            Assert.Null(referencesResponse["error"]);
            Assert.NotNull(referencesResponse["result"]);
            Assert.Contains("ServiceA", referencesResponse["result"]!.ToJsonString());

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
