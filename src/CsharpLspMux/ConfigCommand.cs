using System.Text.Json.Nodes;

namespace CsharpLspMux;

public static class ConfigCommand
{
    public static int Run(string[] subArgs, string workingDir, string? userConfigPath = null)
    {
        if (subArgs.Length == 0)
            return Error("Usage: csharp-lsp-mux config <set|get|list> [args]");

        userConfigPath ??= MuxConfig.GetUserConfigPath();

        return subArgs[0] switch
        {
            "set" => RunSet(subArgs[1..], workingDir, userConfigPath),
            "get" => RunGet(subArgs[1..], workingDir, userConfigPath),
            "list" => RunList(workingDir, userConfigPath),
            _ => Error($"Unknown config subcommand: {subArgs[0]}")
        };
    }

    private static int RunSet(string[] args, string workingDir, string userConfigPath)
    {
        bool global = args.Length > 0 && args[0] == "--global";
        if (global)
            args = args[1..];

        if (args.Length != 2)
            return Error("Usage: csharp-lsp-mux config set [--global] <key> <value>");

        var key = args[0];
        var valueStr = args[1];

        if (key != "max-servers")
            return Error($"Unknown config key: {key}");

        if (!int.TryParse(valueStr, out var value) || value < 1 || value > 100)
            return Error($"Invalid value for max-servers: must be an integer between 1 and 100");

        var filePath = global ? userConfigPath : Path.Combine(workingDir, ".csharp-lsp-mux.json");

        if (global)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        JsonObject node;
        if (File.Exists(filePath))
        {
            try
            {
                node = JsonNode.Parse(File.ReadAllText(filePath))?.AsObject() ?? new JsonObject();
            }
            catch
            {
                Console.Error.WriteLine($"[csharp-lsp-mux] warning: malformed config file '{filePath}', overwriting");
                node = new JsonObject();
            }
        }
        else
        {
            node = new JsonObject();
        }

        node["maxServers"] = value;
        File.WriteAllText(filePath, node.ToJsonString());
        return 0;
    }

    private static int RunGet(string[] args, string workingDir, string userConfigPath)
    {
        if (args.Length != 1)
            return Error("Usage: csharp-lsp-mux config get <key>");

        var key = args[0];
        if (key != "max-servers")
            return Error($"Unknown config key: {key}");

        var cfg = new MuxConfig(workingDir, userConfigPath);
        Console.WriteLine($"max-servers = {cfg.MaxServers} (source: {cfg.MaxServersSource})");
        return 0;
    }

    private static int RunList(string workingDir, string userConfigPath)
    {
        var cfg = new MuxConfig(workingDir, userConfigPath);
        Console.WriteLine($"max-servers = {cfg.MaxServers} (source: {cfg.MaxServersSource})");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
