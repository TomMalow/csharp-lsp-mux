using System.Text.Json.Nodes;

namespace CsharpLspMux;

public interface ILspTransport
{
    Task WriteFrameAsync(byte[] frame);
    Task SendResponseAsync(JsonNode? id, JsonNode result);
}
