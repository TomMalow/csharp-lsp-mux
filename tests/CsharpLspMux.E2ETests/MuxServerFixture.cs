using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.E2ETests;

/// <summary>
/// Boots the mux plus both MonoRepo solutions' Roslyn servers once, opens the fixture files each
/// per-operation test needs, and warms readiness (a hover per solution, so the InitBarrier has
/// fired before any test issues a request) before handing out a ready client. Shared across a
/// test collection so the expensive Roslyn boot happens once instead of once per test.
/// </summary>
public sealed class MuxServerFixture : IAsyncLifetime
{
    private static readonly string[] ServiceAApiPath = ["src", "ServiceA", "ServiceA.Api", "Class1.cs"];
    private static readonly string[] ServiceAConsumerPath = ["src", "ServiceA", "ServiceA.Consumer", "Class1.cs"];
    private static readonly string[] ServiceBWorkerPath = ["src", "ServiceB", "ServiceB.Worker", "Class1.cs"];
    private static readonly string[] ServiceBConsumerPath = ["src", "ServiceB", "ServiceB.Consumer", "Class1.cs"];

    // ServiceAClient / ServiceBWorker class declarations
    private const int ClassDeclarationLine = 2;
    private const int ClassDeclarationChar = 13;

    private Process _process = null!;

    public MonoRepoFixture Repo { get; } = new();
    public LspClient Client { get; private set; } = null!;

    public string ServiceAApiUri { get; private set; } = null!;
    public string ServiceAConsumerUri { get; private set; } = null!;
    public string ServiceBWorkerUri { get; private set; } = null!;
    public string ServiceBConsumerUri { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var psi = new ProcessStartInfo("dotnet", MuxPaths.MuxDll)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["REPO_ROOT"] = Repo.TempDir }
        };

        _process = Process.Start(psi)!;
        Client = new LspClient(_process.StandardInput.BaseStream, _process.StandardOutput.BaseStream);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = cts.Token;

        try
        {
            var initResponse = await Client.SendRequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = new Uri(Repo.TempDir).AbsoluteUri,
                ["capabilities"] = new JsonObject()
            }, ct);
            Assert.NotNull(initResponse);
            Assert.Null(initResponse["error"]);

            await Client.SendNotificationAsync("initialized", new JsonObject(), ct);
            await Client.SendNotificationAsync("workspace/didChangeConfiguration", new JsonObject
            {
                ["settings"] = new JsonObject()
            }, ct);

            ServiceAApiUri = FileUri(ServiceAApiPath);
            ServiceAConsumerUri = FileUri(ServiceAConsumerPath);
            ServiceBWorkerUri = FileUri(ServiceBWorkerPath);
            ServiceBConsumerUri = FileUri(ServiceBConsumerPath);

            await OpenAsync(ServiceAApiUri, ServiceAApiPath, ct);
            await OpenAsync(ServiceAConsumerUri, ServiceAConsumerPath, ct);
            await OpenAsync(ServiceBWorkerUri, ServiceBWorkerPath, ct);
            await OpenAsync(ServiceBConsumerUri, ServiceBConsumerPath, ct);

            // Warm readiness: a hover per solution forces each solution's InitBarrier to have
            // fired before any test issues a request against the shared client.
            await WarmAsync(ServiceAApiUri, ct);
            await WarmAsync(ServiceBWorkerUri, ct);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var shutdownResponse = await Client.SendRequestAsync("shutdown", null, cts.Token);
            Assert.NotNull(shutdownResponse);
            Assert.Null(shutdownResponse["error"]);

            await Client.SendNotificationAsync("exit", null, cts.Token);
            await _process.WaitForExitAsync(cts.Token);
            Assert.Equal(0, _process.ExitCode);
        }
        finally
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
            }
            Repo.Dispose();
        }
    }

    private string FileUri(string[] relativeSegments) =>
        new Uri(Path.Combine([Repo.TempDir, .. relativeSegments])).AbsoluteUri;

    private async Task OpenAsync(string uri, string[] relativeSegments, CancellationToken ct) =>
        await Client.SendNotificationAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["languageId"] = "csharp",
                ["version"] = 1,
                ["text"] = Repo.ReadFile(relativeSegments)
            }
        }, ct);

    private async Task WarmAsync(string uri, CancellationToken ct)
    {
        var hoverResponse = await Client.SendRequestAsync("textDocument/hover", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = ClassDeclarationLine, ["character"] = ClassDeclarationChar }
        }, ct);
        Assert.NotNull(hoverResponse);
        Assert.Null(hoverResponse["error"]);
    }
}

[CollectionDefinition(Name)]
public sealed class MuxServerCollection : ICollectionFixture<MuxServerFixture>
{
    public const string Name = "MuxServer";
}
