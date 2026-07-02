using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsharpLspMux;

namespace CsharpLspMux.E2ETests;

/// <summary>
/// Thin LSP client over a spawned process's stdin/stdout.
/// </summary>
internal sealed class LspClient : IDisposable
{
    private readonly LspTransport _transport;
    private readonly LspFrameReader _reader;
    private int _nextId;

    public LspClient(Stream writeStream, Stream readStream)
    {
        _transport = new LspTransport(writeStream);
        _reader = new LspFrameReader(readStream);
    }

    public async Task<JsonObject?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        var id = ++_nextId;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null)
            message["params"] = @params;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _transport.WriteFrameAsync(bytes);
        return await ReadResponseAsync(id, ct);
    }

    public async Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null)
            message["params"] = @params;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _transport.WriteFrameAsync(bytes);
    }

    private async Task<JsonObject?> ReadResponseAsync(int expectedId, CancellationToken ct)
    {
        while (true)
        {
            var frame = await _reader.ReadFrameAsync(ct);
            if (frame is null) return null;
            // Skip notifications (no id) and server-initiated requests (id + method)
            if (frame["id"] is null) continue;
            if (frame["method"] is not null) continue;
            // JSON round-trip deserialises numbers as long; compare via ToJsonString to avoid type mismatch
            if (frame["id"]?.ToJsonString() == expectedId.ToString()) return frame;
        }
    }

    public void Dispose() { }
}
