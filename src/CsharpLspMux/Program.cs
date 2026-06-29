using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

while (true)
{
    var message = await ReadMessageAsync(stdin);
    if (message is null) break;

    var method = message["method"]?.GetValue<string>();

    if (method == "initialize")
    {
        var id = message["id"];
        await SendResponseAsync(stdout, id, new JsonObject
        {
            ["capabilities"] = new JsonObject(),
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "csharp-lsp-mux",
                ["version"] = "0.1.0"
            }
        });
    }
    else if (method == "shutdown")
    {
        var id = message["id"];
        await SendResponseAsync(stdout, id, JsonValue.Create<object?>(null)!);
    }
    else if (method == "exit")
    {
        break;
    }
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

static async Task SendResponseAsync(Stream stream, JsonNode? id, JsonNode result)
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

    await stream.WriteAsync(header);
    await stream.WriteAsync(body);
    await stream.FlushAsync();
}
