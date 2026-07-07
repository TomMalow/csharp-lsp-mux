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

static LogLevel? ParseLevel(string? s) => s?.ToLowerInvariant() switch
{
    "off" => LogLevel.Off,
    "info" => LogLevel.Info,
    "debug" => LogLevel.Debug,
    _ => null
};

var flagLevel = flagIndex >= 0 ? ParseLevel(logLevelArg) : null;
var envLevel = ParseLevel(Environment.GetEnvironmentVariable("LSP_MUX_LOG_LEVEL"));
var resolvedLevel = flagLevel ?? envLevel ?? LogLevel.Off;
// --log-level flag always writes to stderr; LSP_MUX_LOG_OUTPUT_FILE only applies when using env var
var filePath = flagLevel.HasValue ? null : Environment.GetEnvironmentVariable("LSP_MUX_LOG_OUTPUT_FILE");

var (logger, logFileWriter) = MuxLoggerFactory.Create(resolvedLevel, filePath, Console.Error);

var stdinReader = new LspFrameReader(Console.OpenStandardInput());
var lspTransport = new LspTransport(Console.OpenStandardOutput());

var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT") ?? Directory.GetCurrentDirectory();
var config = new MuxConfig(repoRoot);
var router = new SolutionRouter(repoRoot);
var pool = new ServerPool<ServerSession>(config.MaxServers, CreateServer);

Task<ServerSession> CreateServer(string sln)
{
    IChildServer server = RoslynServerProcess.Start(sln, logger);
    server.OnRelayFrame += frame => new ValueTask(lspTransport.WriteFrameAsync(frame));
    return Task.FromResult(new ServerSession(server));
}
var dispatcher = new MuxDispatcher(router, pool, lspTransport, logger: logger);
pool.OnGracefulShutdown = s => s.Server.ShutdownAsync();

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
