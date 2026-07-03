using System.Reflection;
using CsharpLspMux;

if (args.Length == 1 && args[0] == "--version")
{
    var rawVersion = Assembly.GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
    var version = rawVersion.Split('+')[0];
    Console.WriteLine($"csharp-lsp-mux {version}");
    return;
}

if (args.Length >= 1 && args[0] == "config")
{
    var workingDir = Environment.GetEnvironmentVariable("REPO_ROOT") ?? Directory.GetCurrentDirectory();
    Environment.Exit(ConfigCommand.Run(args[1..], workingDir));
}

var flagIndex = Array.IndexOf(args, "--log-level");
var logLevelArg = flagIndex >= 0 && flagIndex + 1 < args.Length ? args[flagIndex + 1] : null;
var debugFlagEnabled = string.Equals(logLevelArg, "debug", StringComparison.OrdinalIgnoreCase) && flagIndex >= 0;
var debugEnvEnabled = string.Equals(Environment.GetEnvironmentVariable("LSP_MUX_LOG"), "debug", StringComparison.OrdinalIgnoreCase);
var logFilePath = Environment.GetEnvironmentVariable("LSP_MUX_LOG_FILE");

var (logger, logFileWriter) = MuxLoggerFactory.Create(logFilePath, debugFlagEnabled, debugEnvEnabled, Console.Error);

var stdinReader = new LspFrameReader(Console.OpenStandardInput());
var lspTransport = new LspTransport(Console.OpenStandardOutput());

var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT") ?? Directory.GetCurrentDirectory();
var config = new MuxConfig(repoRoot);
var router = new SolutionRouter(repoRoot);
var pool = new ServerPool<IChildServer>(config.MaxServers, CreateServer);

Task<IChildServer> CreateServer(string sln)
{
    IChildServer server = RoslynServerProcess.Start(sln, logger);
    server.OnRelayFrame += frame => new ValueTask(lspTransport.WriteFrameAsync(frame));
    return Task.FromResult(server);
}
var dispatcher = new MuxDispatcher(router, pool, lspTransport, logger: logger);
pool.OnGracefulShutdown = s => s.ShutdownAsync();

try
{
    while (true)
    {
        var message = await stdinReader.ReadFrameAsync();
        if (message is null) break;
        if (!await dispatcher.HandleMessageAsync(message)) break;
    }
}
finally
{
    await (logFileWriter?.DisposeAsync() ?? ValueTask.CompletedTask);
}
