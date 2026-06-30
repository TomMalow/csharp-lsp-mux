using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsharpLspMux;
using Xunit;

namespace CsharpLspMux.Tests;

public class LspTransportTests
{
    private static MemoryStream FramedStream(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(body);
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream EmptyStream() => new();

    [Fact]
    public async Task ReadMessageAsync_ParsesFramedMessage()
    {
        var json = """{"jsonrpc":"2.0","method":"initialized"}""";
        using var stream = FramedStream(json);

        var result = await LspTransport.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("initialized", result["method"]?.GetValue<string>());
    }

    [Fact]
    public async Task ReadMessageAsync_ReturnsNullOnEof()
    {
        using var stream = EmptyStream();

        var result = await LspTransport.ReadMessageAsync(stream, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadMessageAsync_IgnoresUnknownHeaders()
    {
        var json = """{"jsonrpc":"2.0","id":1}""";
        var body = Encoding.UTF8.GetBytes(json);
        var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes($"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n"));
        ms.Write(Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        ms.Write(body);
        ms.Position = 0;

        var result = await LspTransport.ReadMessageAsync(ms, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result["id"]?.GetValue<int>());
    }

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
    public async Task RoundTrip_WriteReadProducesOriginalMessage()
    {
        var pipe = new MemoryStream();
        var transport = new LspTransport(pipe);
        var original = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "$/ping" };
        var frame = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(original));

        await transport.WriteFrameAsync(frame);
        pipe.Position = 0;

        var result = await LspTransport.ReadMessageAsync(pipe, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("$/ping", result["method"]?.GetValue<string>());
    }
}
