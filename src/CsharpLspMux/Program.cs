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

var stdinReader = new LspFrameReader(Console.OpenStandardInput());
var lspTransport = new LspTransport(Console.OpenStandardOutput());

var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT") ?? Directory.GetCurrentDirectory();
var config = new MuxConfig(repoRoot);
var router = new SolutionRouter(repoRoot);
var pool = new ServerPool<IChildServer>(config.MaxServers,
    sln => Task.FromResult<IChildServer>(RoslynServerProcess.Start(sln, lspTransport)));
var dispatcher = new MuxDispatcher(router, pool, lspTransport);
pool.OnGracefulShutdown = s => s.ShutdownAsync();

while (true)
{
    var message = await stdinReader.ReadFrameAsync();
    if (message is null) break;
    if (!await dispatcher.HandleMessageAsync(message)) break;
}
