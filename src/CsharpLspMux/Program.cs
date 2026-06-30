using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsharpLspMux;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT") ?? Directory.GetCurrentDirectory();
var router = new SolutionRouter(repoRoot);
// Serializes all writes to stdout across RoslynServerProcess instances and Program.cs itself.
var stdoutLock = new SemaphoreSlim(1, 1);
// Tracks which server owns each in-flight request ID (serialized to string) for cancel forwarding.
var requestOwners = new Dictionary<string, RoslynServerProcess>();
var serverPool = ServerPool<RoslynServerProcess>.FromEnvironment(
    sln => Task.FromResult(RoslynServerProcess.Start(sln, stdout, stdoutLock)));
serverPool.OnEvict = evicted =>
{
    foreach (var key in requestOwners.Keys.Where(k => requestOwners[k] == evicted).ToList())
        requestOwners.Remove(key);
};
var poolDrained = false;

try
{
    while (true)
    {
        var message = await ReadMessageAsync(stdin);
        if (message is null) break;

        var method = message["method"]?.GetValue<string>();

        if (method == "initialize")
        {
            var id = message["id"];
            await SendResponseAsync(stdout, stdoutLock, id, new JsonObject
            {
                ["capabilities"] = new JsonObject(),
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "csharp-lsp-mux",
                    ["version"] = "0.1.0"
                }
            });
        }
        else if (method == "initialized")
        {
            // acknowledge; no forwarding needed yet
        }
        else if (method is not null && method.StartsWith("textDocument/", StringComparison.Ordinal))
        {
            var uri = message["params"]?["textDocument"]?["uri"]?.GetValue<string>();
            if (uri is not null)
            {
                var filePath = new Uri(uri).LocalPath;
                var solutionPath = router.Route(filePath);

                if (solutionPath is not null)
                {
                    var server = await serverPool.GetOrAddAsync(solutionPath);
                    var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                    if (message["id"] is JsonNode requestId)
                        requestOwners[JsonNodeToKey(requestId)] = server;
                    await server.ForwardRequestAsync(raw);
                }
                else
                {
                    // No owning solution found — return null result for requests, ignore notifications
                    if (message["id"] is JsonNode requestId)
                        await SendResponseAsync(stdout, stdoutLock, requestId, JsonValue.Create<object?>(null)!);
                }
            }
        }
        else if (method == "$/cancelRequest")
        {
            var cancelId = message["params"]?["id"];
            if (cancelId is not null)
            {
                var key = JsonNodeToKey(cancelId);
                if (requestOwners.TryGetValue(key, out var owner))
                {
                    requestOwners.Remove(key);
                    var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                    await owner.ForwardRequestAsync(raw);
                }
            }
        }
        else if (method == "workspace/symbol")
        {
            var requestId = message["id"];
            var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            var servers = serverPool.ActiveServers.ToList();

            if (servers.Count == 0)
            {
                if (requestId is not null)
                    await SendResponseAsync(stdout, stdoutLock, requestId, new JsonArray());
            }
            else
            {
                var responses = await Task.WhenAll(servers.Select(s => s.SendAndReceiveAsync(raw)));
                var merged = new JsonArray();
                foreach (var resp in responses)
                {
                    var parsed = JsonSerializer.Deserialize<JsonObject>(resp);
                    if (parsed?["result"] is JsonArray arr)
                        foreach (var item in arr)
                            merged.Add(item?.DeepClone());
                }
                if (requestId is not null)
                    await SendResponseAsync(stdout, stdoutLock, requestId, merged);
            }
        }
        else if (method == "workspace/didChangeWatchedFiles")
        {
            var changes = message["params"]?["changes"]?.AsArray();
            if (changes is not null)
            {
                foreach (var change in changes)
                {
                    var uriStr = change?["uri"]?.GetValue<string>();
                    if (uriStr is null) continue;
                    var path = new Uri(uriStr).LocalPath;
                    var ext = Path.GetExtension(path);
                    if (ext is ".sln" or ".slnx" or ".csproj")
                        router.InvalidateCache(path);
                }
            }
        }
        else if (method == "shutdown")
        {
            var id = message["id"];
            await serverPool.DisposeAllAsync();
            poolDrained = true;
            await SendResponseAsync(stdout, stdoutLock, id, JsonValue.Create<object?>(null)!);
        }
        else if (method == "exit")
        {
            break;
        }
    }
}
finally
{
    if (!poolDrained)
        await serverPool.DisposeAllAsync();
}

static async Task<JsonObject?> ReadMessageAsync(Stream stream)
{
    int contentLength = -1;

    while (true)
    {
        var line = await ReadLineAsync(stream);
        if (line is null) return null;
        if (line.Length == 0) break;

        if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase))
            contentLength = int.Parse(line["Content-Length: ".Length..]);
    }

    if (contentLength < 0) return null;

    var buffer = new byte[contentLength];
    var totalRead = 0;
    while (totalRead < contentLength)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(totalRead));
        if (read == 0) return null;
        totalRead += read;
    }

    return JsonSerializer.Deserialize<JsonObject>(buffer);
}

static async Task<string?> ReadLineAsync(Stream stream)
{
    var sb = new StringBuilder();
    var buf = new byte[1];
    while (true)
    {
        var read = await stream.ReadAsync(buf.AsMemory(0, 1));
        if (read == 0) return null;
        var ch = (char)buf[0];
        if (ch == '\n') return sb.ToString().TrimEnd('\r');
        sb.Append(ch);
    }
}

static string JsonNodeToKey(JsonNode node) => node.ToJsonString();

static async Task SendResponseAsync(Stream stream, SemaphoreSlim streamLock, JsonNode? id, JsonNode result)
{
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    var json = JsonSerializer.Serialize(response);
    var body = Encoding.UTF8.GetBytes(json);
    var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

    await streamLock.WaitAsync();
    try
    {
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
    finally
    {
        streamLock.Release();
    }
}
