using CsharpLspMux;

var stdinReader = new LspFrameReader(Console.OpenStandardInput());
var lspTransport = new LspTransport(Console.OpenStandardOutput());

var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT") ?? Directory.GetCurrentDirectory();
var router = new SolutionRouter(repoRoot);
var pool = ServerPool<IChildServer>.FromEnvironment(
    sln => Task.FromResult<IChildServer>(RoslynServerProcess.Start(sln, lspTransport)));
var dispatcher = new MuxDispatcher(router, pool, lspTransport);
pool.OnEvict = dispatcher.NotifyEviction;

while (true)
{
    var message = await stdinReader.ReadFrameAsync();
    if (message is null) break;
    if (!await dispatcher.HandleMessageAsync(message)) break;
}
