using System.Text.Json.Nodes;

namespace CsharpLspMux;

public sealed class MuxConfig
{
    public int MaxServers { get; }

    public MuxConfig(string repoRoot)
    {
        var filePath = Path.Combine(repoRoot, ".csharp-lsp-mux.json");
        int? fileMax = null;

        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var node = JsonNode.Parse(json);
                var raw = node?["maxServers"]?.GetValue<int>();
                if (raw is > 0)
                    fileMax = raw;
            }
            catch
            {
                Console.Error.WriteLine($"[csharp-lsp-mux] warning: malformed config file '{filePath}', using defaults");
            }
        }

        var envRaw = Environment.GetEnvironmentVariable("LSP_ROUTER_MAX_SERVERS");
        int? envMax = int.TryParse(envRaw, out var n) && n > 0 ? n : null;

        MaxServers = envMax ?? fileMax ?? 10;
    }
}
