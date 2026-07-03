using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

public static class FrameWriterExtensions
{
    /// <summary>Serializes and writes a JSON-RPC success response frame.</summary>
    public static Task SendResponseAsync(this IFrameWriter writer, JsonNode? id, JsonNode result,
        CancellationToken ct = default)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        return writer.WriteFrameAsync(body, ct);
    }

    /// <summary>Serializes and writes a JSON-RPC error response frame.</summary>
    public static Task SendErrorAsync(this IFrameWriter writer, JsonNode? id, int code, string message,
        CancellationToken ct = default)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        return writer.WriteFrameAsync(body, ct);
    }
}
