using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CsharpLspMux.Tests;

public class FrameTests
{
    private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void FromWire_ExposesMethodAndId()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","id":1,"method":"textDocument/hover"}"""));

        Assert.Equal("textDocument/hover", frame.Method);
        Assert.Equal(1, frame.Id?.GetValue<int>());
    }

    [Fact]
    public void FromWire_Json_ParsesEnvelope()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","method":"initialized"}"""));

        Assert.Equal("2.0", frame.Json["jsonrpc"]?.GetValue<string>());
        Assert.Equal("initialized", frame.Json["method"]?.GetValue<string>());
    }

    [Fact]
    public void FromWire_Wire_ReturnsOriginalBytes()
    {
        var original = Bytes("""{"jsonrpc":"2.0","method":"$/ping"}""");
        var frame = Frame.FromWire(original);

        Assert.True(frame.Wire.Span.SequenceEqual(original.Span));
    }

    [Fact]
    public void FromJson_Wire_SerializesEnvelope()
    {
        var json = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "$/ping" };
        var frame = Frame.FromJson(json);

        var reparsed = JsonSerializer.Deserialize<JsonObject>(frame.Wire.Span)!;
        Assert.Equal("$/ping", reparsed["method"]?.GetValue<string>());
    }

    [Fact]
    public void FromJson_Json_ReturnsSameObject()
    {
        var json = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "$/ping" };
        var frame = Frame.FromJson(json);

        Assert.Same(json, frame.Json);
    }

    [Fact]
    public void FromWire_Wire_IsIdempotent()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","method":"$/ping"}"""));

        var first = frame.Wire;
        var second = frame.Wire;

        Assert.True(first.Span.SequenceEqual(second.Span));
    }

    [Fact]
    public void FromJson_Wire_IsIdempotent()
    {
        var frame = Frame.FromJson(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "$/ping" });

        var first = frame.Wire;
        var second = frame.Wire;

        Assert.True(first.Span.SequenceEqual(second.Span));
    }

    [Fact]
    public void FromWire_Json_IsIdempotent()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","method":"$/ping"}"""));

        Assert.Same(frame.Json, frame.Json);
    }

    [Fact]
    public void IsRequest_WhenIdPresent_IsTrue()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","id":1,"method":"textDocument/hover"}"""));

        Assert.True(frame.IsRequest);
        Assert.False(frame.IsNotification);
    }

    [Fact]
    public void IsNotification_WhenIdAbsent_IsTrue()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","method":"textDocument/didOpen"}"""));

        Assert.True(frame.IsNotification);
        Assert.False(frame.IsRequest);
    }

    [Fact]
    public void WithId_ReturnsNewFrameWithRewrittenId()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","id":1,"method":"workspace/symbol"}"""));

        var rewritten = frame.WithId("__mux_1");

        Assert.Equal("__mux_1", rewritten.Id?.GetValue<string>());
        Assert.Equal(1, frame.Id?.GetValue<int>()); // original untouched
    }

    [Fact]
    public void WithId_SerializesRewrittenIdToWire()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","id":1,"method":"workspace/symbol"}"""));

        var rewritten = frame.WithId("__mux_1");

        var reparsed = JsonSerializer.Deserialize<JsonObject>(rewritten.Wire.Span)!;
        Assert.Equal("__mux_1", reparsed["id"]?.GetValue<string>());
        Assert.Equal("workspace/symbol", reparsed["method"]?.GetValue<string>());
    }

    [Fact]
    public void WithId_NullId_ProducesNotification()
    {
        var frame = Frame.FromWire(Bytes("""{"jsonrpc":"2.0","id":1,"method":"workspace/symbol"}"""));

        var rewritten = frame.WithId(null);

        Assert.True(rewritten.IsNotification);
    }
}
