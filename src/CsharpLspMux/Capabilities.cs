using System.Text.Json.Nodes;

namespace CsharpLspMux;

/// <summary>
/// Single source of truth for the LSP capabilities the mux advertises in both directions: the
/// client-facing <c>initialize</c> response sent to Claude Code, and the Roslyn-facing
/// <c>initialize</c> request sent to each child <c>roslyn-language-server</c>. Feature providers
/// are declared once and projected onto both shapes so the two sides cannot silently drift.
/// Operational capabilities exist to make the tool function (progress, sync) rather than expose
/// a user-facing LSP feature, and are exempt from the feature-provider mirror.
/// </summary>
public static class Capabilities
{
    /// <summary>Where a capability lives and what it's advertised as, on one side of the client/Roslyn boundary.</summary>
    public sealed record Projection(string[] Path, Func<JsonNode> Value);

    /// <summary>An LSP feature advertised on both the client-facing and Roslyn-facing sides.</summary>
    public sealed record FeatureProvider(string Name, Projection ClientSide, Projection RoslynSide);

    /// <summary>A capability that serves the tool's own operation rather than a mirrored LSP feature; either side may be absent when that side has no counterpart.</summary>
    public sealed record OperationalCapability(string Name, Projection? ClientSide, Projection? RoslynSide);

    private static JsonNode DynamicRegistrationFalse() => new JsonObject { ["dynamicRegistration"] = false };

    /// <summary>Feature providers currently advertised, mirrored on both sides.</summary>
    public static readonly IReadOnlyList<FeatureProvider> FeatureProviders = new[]
    {
        new FeatureProvider(
            "hover",
            ClientSide: new(new[] { "hoverProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "hover" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "definition",
            ClientSide: new(new[] { "definitionProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "definition" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "references",
            ClientSide: new(new[] { "referencesProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "references" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "implementation",
            ClientSide: new(new[] { "implementationProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "implementation" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "documentSymbol",
            ClientSide: new(new[] { "documentSymbolProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "documentSymbol" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "callHierarchy",
            ClientSide: new(new[] { "callHierarchyProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "callHierarchy" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "workspaceSymbol",
            ClientSide: new(new[] { "workspaceSymbolProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "workspace", "symbol" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "completion",
            ClientSide: new(new[] { "completionProvider" }, () => new JsonObject { ["triggerCharacters"] = new JsonArray(".", " ") }),
            RoslynSide: new(new[] { "textDocument", "completion" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "signatureHelp",
            ClientSide: new(new[] { "signatureHelpProvider" }, () => new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",") }),
            RoslynSide: new(new[] { "textDocument", "signatureHelp" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "rename",
            ClientSide: new(new[] { "renameProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "rename" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "codeAction",
            ClientSide: new(new[] { "codeActionProvider" }, () => JsonValue.Create(true)),
            RoslynSide: new(new[] { "textDocument", "codeAction" }, DynamicRegistrationFalse)),
        new FeatureProvider(
            "diagnostic",
            ClientSide: new(new[] { "diagnosticProvider" }, () => new JsonObject { ["interFileDependencies"] = true, ["workspaceDiagnostics"] = false }),
            RoslynSide: new(new[] { "textDocument", "diagnostic" }, DynamicRegistrationFalse)),
    };

    /// <summary>Operational capabilities exempt from the feature-provider mirror.</summary>
    public static readonly IReadOnlyList<OperationalCapability> OperationalCapabilities = new[]
    {
        new OperationalCapability(
            "textSynchronization",
            ClientSide: new(new[] { "textDocumentSync" }, () => new JsonObject { ["openClose"] = true, ["change"] = 2 }),
            RoslynSide: new(new[] { "textDocument", "synchronization" }, DynamicRegistrationFalse)),
        new OperationalCapability(
            "workDoneProgress",
            ClientSide: null,
            RoslynSide: new(new[] { "window", "workDoneProgress" }, () => JsonValue.Create(true))),
    };

    /// <summary>Builds the <c>capabilities</c> object for the client-facing <c>initialize</c> response sent to Claude Code.</summary>
    public static JsonObject BuildClientFacingCapabilities() =>
        Build(FeatureProviders.Select(f => f.ClientSide), OperationalCapabilities.Select(o => o.ClientSide));

    /// <summary>Builds the <c>capabilities</c> object for the Roslyn-facing <c>initialize</c> request sent to each child server.</summary>
    public static JsonObject BuildRoslynFacingCapabilities() =>
        Build(FeatureProviders.Select(f => f.RoslynSide), OperationalCapabilities.Select(o => o.RoslynSide));

    private static JsonObject Build(IEnumerable<Projection> featureProjections, IEnumerable<Projection?> operationalProjections)
    {
        var caps = new JsonObject();
        foreach (var projection in featureProjections)
            SetAtPath(caps, projection.Path, projection.Value());
        foreach (var projection in operationalProjections)
            if (projection is not null)
                SetAtPath(caps, projection.Path, projection.Value());
        return caps;
    }

    /// <summary>Reads the value at a capability path (e.g. <c>["textDocument","hover"]</c>) from a built capabilities object, or null if any segment is missing. Test-only: production call sites only need the built object as a whole.</summary>
    internal static JsonNode? GetAtPath(JsonObject root, string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            if (current is not JsonObject obj)
                return null;
            current = obj[segment];
        }
        return current;
    }

    private static void SetAtPath(JsonObject root, string[] path, JsonNode value)
    {
        var current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current[path[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[path[i]] = next;
            }
            current = next;
        }
        current[path[^1]] = value;
    }
}
