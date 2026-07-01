using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

public sealed class InitShutdownTests : IDisposable
{
    private readonly MonoRepoFixture _fixture;

    public InitShutdownTests()
    {
        _fixture = new MonoRepoFixture();
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task InitShutdown_BinaryRespondsToInitializeAndExitsCleanly()
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
            Assert.NotNull(initResponse["result"]);
            var serverInfo = initResponse["result"]?["serverInfo"];
            Assert.Equal("csharp-lsp-mux", serverInfo?["name"]?.GetValue<string>());

            // 2. initialized notification
            await client.SendNotificationAsync("initialized", new JsonObject(), ct);

            // 3. workspace/didChangeConfiguration
            await client.SendNotificationAsync("workspace/didChangeConfiguration", new JsonObject
            {
                ["settings"] = new JsonObject()
            }, ct);

            // 4. shutdown
            var shutdownResponse = await client.SendRequestAsync("shutdown", null, ct);
            Assert.NotNull(shutdownResponse);
            Assert.Null(shutdownResponse["error"]);

            // 5. exit
            await client.SendNotificationAsync("exit", null, ct);

            await process.WaitForExitAsync(ct);
            Assert.Equal(0, process.ExitCode);
        }
        catch
        {
            // Kill the process to avoid leaking it on test failure or cancellation
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    public void Dispose() => _fixture.Dispose();
}
