using System.Text.Json.Nodes;

namespace CsharpLspMux;

public sealed class MuxConfig
{
    public int MaxServers { get; }
    public string MaxServersSource { get; }

    public MuxConfig(string repoRoot, string? userConfigPath = null)
    {
        userConfigPath ??= GetUserConfigPath();

        int? fileMax = ReadMaxServers(Path.Combine(repoRoot, ".csharp-lsp-mux.json"));
        int? userMax = ReadMaxServers(userConfigPath);

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
        else if (userMax != null)
        {
            MaxServers = userMax.Value;
            MaxServersSource = "user";
        }
        else
        {
            MaxServers = 10;
            MaxServersSource = "default";
        }
    }

    public static string GetUserConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "csharp-lsp-mux", "config.json");
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrEmpty(xdgConfigHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdgConfigHome;
        return Path.Combine(configHome, "csharp-lsp-mux", "config.json");
    }

    private static int? ReadMaxServers(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = File.ReadAllText(filePath);
            var node = JsonNode.Parse(json);
            var raw = node?["maxServers"]?.GetValue<int>();
            if (raw is > 0)
                return raw;
        }
        catch
        {
            Console.Error.WriteLine($"[csharp-lsp-mux] warning: malformed config file '{filePath}', using defaults");
        }

        return null;
    }
}
