using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsharpLspMux;
using Xunit;

namespace CsharpLspMux.Tests;

public class LspTransportTests
{
    [Fact]
    public async Task WriteFrameAsync_WritesContentLengthFramedBytes()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0"}""");
        using var output = new MemoryStream();
        var transport = new LspTransport(output);

        await transport.WriteFrameAsync(body);

        output.Position = 0;
        var written = Encoding.UTF8.GetString(output.ToArray());
        Assert.StartsWith($"Content-Length: {body.Length}\r\n\r\n", written);
        Assert.EndsWith("""{"jsonrpc":"2.0"}""", written);
    }

    [Fact]
    public async Task SendResponseAsync_WritesValidJsonRpcResponse()
    {
        using var output = new MemoryStream();
        var transport = new LspTransport(output);

        await transport.SendResponseAsync(JsonValue.Create(42), new JsonObject { ["ok"] = true });

        output.Position = 0;
        var text = Encoding.UTF8.GetString(output.ToArray());
        var headerEnd = text.IndexOf("\r\n\r\n") + 4;
        var json = text[headerEnd..];
        var parsed = JsonSerializer.Deserialize<JsonObject>(json)!;
        Assert.Equal("2.0", parsed["jsonrpc"]?.GetValue<string>());
        Assert.Equal(42, parsed["id"]?.GetValue<int>());
        Assert.True(parsed["result"]?["ok"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SendErrorAsync_WritesValidJsonRpcError()
    {
        using var output = new MemoryStream();
        var transport = new LspTransport(output);

        await transport.SendErrorAsync(JsonValue.Create(7), -32001, "No solution found for file: /repo/Foo.cs");

        output.Position = 0;
        var text = Encoding.UTF8.GetString(output.ToArray());
        var headerEnd = text.IndexOf("\r\n\r\n") + 4;
        var json = text[headerEnd..];
        var parsed = JsonSerializer.Deserialize<JsonObject>(json)!;
        Assert.Equal("2.0", parsed["jsonrpc"]?.GetValue<string>());
        Assert.Equal(7, parsed["id"]?.GetValue<int>());
        Assert.False(parsed.ContainsKey("result"));
        Assert.Equal(-32001, parsed["error"]?["code"]?.GetValue<int>());
        Assert.Equal("No solution found for file: /repo/Foo.cs", parsed["error"]?["message"]?.GetValue<string>());
    }
}
