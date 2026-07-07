using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpLspMux;

/// <summary>
/// Immutable JSON-RPC frame. Born from either the wire (bytes) or synthesized JSON;
/// the other representation is materialized lazily and cached. A frame is forwarded
/// using the representation it was born with, so an inbound wire frame passes through
/// byte-identical rather than being re-serialized.
/// </summary>
public sealed class Frame
{
    private readonly Lazy<ReadOnlyMemory<byte>> _wire;
    private readonly Lazy<JsonObject> _json;

    private Frame(Lazy<ReadOnlyMemory<byte>> wire, Lazy<JsonObject> json)
    {
        _wire = wire;
        _json = json;
    }

    /// <summary>Builds a frame from wire bytes. Parsing into <see cref="Json"/> is deferred until first read.</summary>
    public static Frame FromWire(ReadOnlyMemory<byte> bytes) => new(
        new Lazy<ReadOnlyMemory<byte>>(() => bytes, LazyThreadSafetyMode.ExecutionAndPublication),
        new Lazy<JsonObject>(() => JsonSerializer.Deserialize<JsonObject>(bytes.Span)!, LazyThreadSafetyMode.ExecutionAndPublication));

    /// <summary>Builds a frame from a synthesized JSON envelope. Serializing into <see cref="Wire"/> is deferred until first read.</summary>
    public static Frame FromJson(JsonObject json) => new(
        new Lazy<ReadOnlyMemory<byte>>(() => JsonSerializer.SerializeToUtf8Bytes(json), LazyThreadSafetyMode.ExecutionAndPublication),
        new Lazy<JsonObject>(() => json, LazyThreadSafetyMode.ExecutionAndPublication));

    /// <summary>The frame's wire bytes — the original bytes if born from the wire, otherwise serialized once and cached.</summary>
    public ReadOnlyMemory<byte> Wire => _wire.Value;

    /// <summary>The frame's JSON envelope — parsed once and cached if born from the wire.</summary>
    public JsonObject Json => _json.Value;

    /// <summary>The JSON-RPC method name, or null for a response.</summary>
    public string? Method => Json["method"]?.GetValue<string>();

    /// <summary>The JSON-RPC id, or null for a notification.</summary>
    public JsonNode? Id => Json["id"];

    /// <summary>An LSP request carries an id; a notification does not.</summary>
    public bool IsRequest => Id is not null;

    /// <summary>An LSP notification carries no id.</summary>
    public bool IsNotification => Id is null;

    /// <summary>The sole mutation: clones the JSON envelope with a rewritten id, returning a new json-origin frame.</summary>
    public Frame WithId(JsonNode? id)
    {
        var clone = (JsonObject)Json.DeepClone();
        clone["id"] = id;
        return FromJson(clone);
    }
}
