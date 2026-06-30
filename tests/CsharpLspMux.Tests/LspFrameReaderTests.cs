using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsharpLspMux;
using Xunit;

namespace CsharpLspMux.Tests;

public class LspFrameReaderTests
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

    [Fact]
    public async Task ReadFrameAsync_ParsesFramedMessage()
    {
        var json = """{"jsonrpc":"2.0","method":"initialized"}""";
        using var stream = FramedStream(json);
        var reader = new LspFrameReader(stream);

        var result = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("initialized", result["method"]?.GetValue<string>());
    }

    [Fact]
    public async Task ReadFrameAsync_ReturnsNullOnEof()
    {
        using var stream = new MemoryStream();
        var reader = new LspFrameReader(stream);

        var result = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadFrameAsync_IgnoresUnknownHeaders()
    {
        var json = """{"jsonrpc":"2.0","id":1}""";
        var body = Encoding.UTF8.GetBytes(json);
        var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n"));
        ms.Write(Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        ms.Write(body);
        ms.Position = 0;
        var reader = new LspFrameReader(ms);

        var result = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result["id"]?.GetValue<int>());
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

        var reader = new LspFrameReader(pipe);
        var result = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("$/ping", result["method"]?.GetValue<string>());
    }
}
