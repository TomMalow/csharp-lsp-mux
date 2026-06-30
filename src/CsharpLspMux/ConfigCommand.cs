using System.Text.Json.Nodes;

namespace CsharpLspMux;

public static class ConfigCommand
{
    public static int Run(string[] subArgs, string workingDir)
    {
        if (subArgs.Length == 0)
            return Error("Usage: csharp-lsp-mux config <set|get|list> [args]");

        return subArgs[0] switch
        {
            "set" => RunSet(subArgs[1..], workingDir),
            "get" => RunGet(subArgs[1..], workingDir),
            "list" => RunList(workingDir),
            _ => Error($"Unknown config subcommand: {subArgs[0]}")
        };
    }

    private static int RunSet(string[] args, string workingDir)
    {
        if (args.Length != 2)
            return Error("Usage: csharp-lsp-mux config set <key> <value>");

        var key = args[0];
        var valueStr = args[1];

        if (key != "max-servers")
            return Error($"Unknown config key: {key}");

        if (!int.TryParse(valueStr, out var value) || value < 1 || value > 100)
            return Error($"Invalid value for max-servers: must be an integer between 1 and 100");

        var filePath = Path.Combine(workingDir, ".csharp-lsp-mux.json");
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

    private static int RunGet(string[] args, string workingDir)
    {
        if (args.Length != 1)
            return Error("Usage: csharp-lsp-mux config get <key>");

        var key = args[0];
        if (key != "max-servers")
            return Error($"Unknown config key: {key}");

        var cfg = new MuxConfig(workingDir);
        Console.WriteLine($"max-servers = {cfg.MaxServers} (source: {cfg.MaxServersSource})");
        return 0;
    }

    private static int RunList(string workingDir)
    {
        var cfg = new MuxConfig(workingDir);
        Console.WriteLine($"max-servers = {cfg.MaxServers} (source: {cfg.MaxServersSource})");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
