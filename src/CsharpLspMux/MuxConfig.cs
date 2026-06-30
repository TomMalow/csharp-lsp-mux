using System.Text.Json.Nodes;

namespace CsharpLspMux;

public sealed class MuxConfig
{
    public int MaxServers { get; }
    public string MaxServersSource { get; }

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

        if (envMax != null)
        {
            MaxServers = envMax.Value;
            MaxServersSource = "env";
        }
        else if (fileMax != null)
        {
            MaxServers = fileMax.Value;
            MaxServersSource = "file";
        }
        else
        {
            MaxServers = 10;
            MaxServersSource = "default";
        }
    }
}
