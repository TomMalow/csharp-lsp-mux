using System.Text.Json.Nodes;

namespace CsharpLspMux;

public interface IFrameReader
{
    Task<JsonObject?> ReadFrameAsync(CancellationToken ct = default);
}
