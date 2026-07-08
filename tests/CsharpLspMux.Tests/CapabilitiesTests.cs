using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class CapabilitiesTests
{
    [Fact]
    public void BuildClientFacingCapabilities_MatchesCurrentContract()
    {
        var caps = Capabilities.BuildClientFacingCapabilities();

        Assert.True(caps["hoverProvider"]?.GetValue<bool>());
        Assert.True(caps["definitionProvider"]?.GetValue<bool>());
        Assert.True(caps["referencesProvider"]?.GetValue<bool>());
        Assert.True(caps["implementationProvider"]?.GetValue<bool>());
        Assert.True(caps["documentSymbolProvider"]?.GetValue<bool>());
        Assert.True(caps["callHierarchyProvider"]?.GetValue<bool>());
        Assert.True(caps["workspaceSymbolProvider"]?.GetValue<bool>());
        Assert.True(caps["renameProvider"]?.GetValue<bool>());
        Assert.True(caps["codeActionProvider"]?.GetValue<bool>());
        Assert.True(caps["textDocumentSync"]?["openClose"]?.GetValue<bool>());
        Assert.Equal(2, caps["textDocumentSync"]?["change"]?.GetValue<int>());

        var completionTriggers = caps["completionProvider"]!["triggerCharacters"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains(".", completionTriggers);
        Assert.Contains(" ", completionTriggers);

        var signatureTriggers = caps["signatureHelpProvider"]!["triggerCharacters"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Contains("(", signatureTriggers);
        Assert.Contains(",", signatureTriggers);

        Assert.True(caps["diagnosticProvider"]?["interFileDependencies"]?.GetValue<bool>());
        Assert.False(caps["diagnosticProvider"]?["workspaceDiagnostics"]?.GetValue<bool>());

        // Capability-lock: exactly this key set advertised today; adding/removing a
        // capability must be a conscious edit to this test, not a silent side effect.
        Assert.Equal(
            new[]
            {
                "textDocumentSync", "hoverProvider", "definitionProvider", "referencesProvider",
                "implementationProvider", "documentSymbolProvider", "callHierarchyProvider", "workspaceSymbolProvider",
                "completionProvider", "signatureHelpProvider", "renameProvider", "codeActionProvider",
                "diagnosticProvider",
            }.OrderBy(k => k),
            caps.Select(kv => kv.Key).OrderBy(k => k));
    }

    [Fact]
    public void BuildRoslynFacingCapabilities_MatchesCurrentContract()
    {
        var caps = Capabilities.BuildRoslynFacingCapabilities();

        Assert.NotNull(caps["workspace"]?["symbol"]);
        Assert.NotNull(caps["textDocument"]?["synchronization"]);
        Assert.NotNull(caps["textDocument"]?["hover"]);
        Assert.NotNull(caps["textDocument"]?["definition"]);
        Assert.NotNull(caps["textDocument"]?["references"]);
        Assert.NotNull(caps["textDocument"]?["implementation"]);
        Assert.NotNull(caps["textDocument"]?["documentSymbol"]);
        Assert.NotNull(caps["textDocument"]?["callHierarchy"]);
        Assert.NotNull(caps["textDocument"]?["completion"]);
        Assert.NotNull(caps["textDocument"]?["signatureHelp"]);
        Assert.NotNull(caps["textDocument"]?["rename"]);
        Assert.NotNull(caps["textDocument"]?["codeAction"]);
        Assert.NotNull(caps["textDocument"]?["diagnostic"]);
        Assert.True(caps["window"]?["workDoneProgress"]?.GetValue<bool>());

        // Capability-lock, Roslyn-facing side.
        Assert.Equal(
            new[]
            {
                "synchronization", "hover", "definition", "references", "implementation",
                "documentSymbol", "callHierarchy", "completion", "signatureHelp", "rename", "codeAction", "diagnostic",
            }.OrderBy(k => k),
            ((JsonObject)caps["textDocument"]!).Select(kv => kv.Key).OrderBy(k => k));
        Assert.Equal(new[] { "symbol" }, ((JsonObject)caps["workspace"]!).Select(kv => kv.Key));
        Assert.Equal(new[] { "workDoneProgress" }, ((JsonObject)caps["window"]!).Select(kv => kv.Key));
    }

    [Fact]
    public void FeatureProviders_MirrorAcrossBothSides()
    {
        var client = Capabilities.BuildClientFacingCapabilities();
        var roslyn = Capabilities.BuildRoslynFacingCapabilities();

        foreach (var feature in Capabilities.FeatureProviders)
        {
            Assert.NotNull(Capabilities.GetAtPath(client, feature.ClientSide.Path));
            Assert.NotNull(Capabilities.GetAtPath(roslyn, feature.RoslynSide.Path));
        }
    }

    [Fact]
    public void OperationalCapabilities_AreExemptFromMirror()
    {
        var client = Capabilities.BuildClientFacingCapabilities();
        var roslyn = Capabilities.BuildRoslynFacingCapabilities();

        // window.workDoneProgress is advertised Roslyn-facing only, by design — Claude
        // Code has no use for knowing the mux's own progress-reporting capability to
        // Roslyn, unlike every feature provider which appears on both sides.
        Assert.True(roslyn["window"]?["workDoneProgress"]?.GetValue<bool>());
        Assert.Null(client["window"]);
    }
}
