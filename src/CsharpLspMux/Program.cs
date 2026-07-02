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
var logLevelEnv = Environment.GetEnvironmentVariable("LSP_MUX_LOG");
// Flag takes precedence; env var used only when flag is absent.
var logLevel = flagIndex >= 0 ? logLevelArg : logLevelEnv;
var loggingEnabled = string.Equals(logLevel, "debug", StringComparison.OrdinalIgnoreCase);
var logger = loggingEnabled ? new MuxLogger(enabled: true, Console.Error) : null;

var stdinReader = new LspFrameReader(Console.OpenStandardInput());
var lspTransport = new LspTransport(Console.OpenStandardOutput());

var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT") ?? Directory.GetCurrentDirectory();
var config = new MuxConfig(repoRoot);
var router = new SolutionRouter(repoRoot);
var pool = new ServerPool<IChildServer>(config.MaxServers,
    sln => Task.FromResult<IChildServer>(RoslynServerProcess.Start(sln, lspTransport, logger)));
var dispatcher = new MuxDispatcher(router, pool, lspTransport, logger: logger);
pool.OnGracefulShutdown = s => s.ShutdownAsync();

while (true)
{
    var message = await stdinReader.ReadFrameAsync();
    if (message is null) break;
    if (!await dispatcher.HandleMessageAsync(message)) break;
}
