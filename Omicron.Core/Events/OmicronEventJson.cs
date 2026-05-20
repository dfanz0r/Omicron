using System.Buffers;
using System.Text.Json;
using Omicron.Core.Content;

namespace Omicron.Core.Events;

/// <summary>
///     Centralized JSON serializer options and helpers for Omicron events.
///     All event serialization paths (store, session resume, fork, export)
///     should use this class to ensure consistent converter registration and
///     <c>$type</c> discriminator handling.
/// </summary>
internal static class OmicronEventJson
{
    /// <summary>Get the shared singleton options instance.</summary>
    public static JsonSerializerOptions Default { get; } = CreateOptions();

    /// <summary>Create a new <see cref="JsonSerializerOptions" /> with standard event settings.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            Converters =
            {
                new ContentBlockJsonConverter()
            }
        };
    }

    // ============================================================
    // Event serialization helpers
    // ============================================================

    /// <summary>Serialize an <see cref="OmicronEvent" /> to UTF-8 JSON bytes with a <c>$type</c> discriminator.</summary>
    /// <remarks>
    ///     Injects the <c>$type</c> field via direct byte insertion into the
    ///     serialized concrete-typed JSON, avoiding a parse/rewrite round-trip.
    /// </remarks>
    public static byte[] SerializeEvent(OmicronEvent evt)
    {
        string typeName = OmicronEventRegistry.GetName(evt.GetType());
        Type evtType = evt.GetType();

        // Step 1: serialize the concrete event type to bytes
        // Output is like: { "envelope":..., "delta":..., ... }
        var payloadBuffer = new ArrayBufferWriter<byte>();
        using (var innerWriter = new Utf8JsonWriter(payloadBuffer))
        {
            JsonSerializer.Serialize(innerWriter, evt, evtType, Default);
            innerWriter.Flush();
        }

        // Step 2: encode just the $type field using a tiny temp writer
        // Output is: { "$type":"TypeName" }
        var typeFieldBuffer = new ArrayBufferWriter<byte>();
        using (var typeWriter = new Utf8JsonWriter(typeFieldBuffer))
        {
            typeWriter.WriteStartObject();
            typeWriter.WriteString("$type"u8, typeName);
            typeWriter.WriteEndObject();
            typeWriter.Flush();
        }

        // Step 3: inject the $type interior right after the opening '{', before
        // the concrete payload interior, separated by a comma.
        //   raw payload:    { <concrete-props> }
        //   type interior:  "$type":"TypeName"
        //   result:         { "$type":"TypeName", <concrete-props> }
        ReadOnlySpan<byte> payload = payloadBuffer.WrittenSpan;
        ReadOnlySpan<byte> typeField = typeFieldBuffer.WrittenSpan;

        // typeField = {"$type":"TypeName"}, we want just the interior
        ReadOnlySpan<byte> typeInterior = typeField[1..^1];

        // result = { + typeInterior + , + payload interior + }
        byte[] result = new byte[1 + typeInterior.Length + 1 + payload.Length - 2 + 1];
        int pos = 0;
        result[pos++] = (byte)'{';
        typeInterior.CopyTo(result.AsSpan(pos));
        pos += typeInterior.Length;
        result[pos++] = (byte)',';
        payload.Slice(1, payload.Length - 2).CopyTo(result.AsSpan(pos));
        pos += payload.Length - 2;
        result[pos] = (byte)'}';

        return result;
    }

    /// <summary>Deserialize an <see cref="OmicronEvent" /> from UTF-8 JSON bytes with a <c>$type</c> discriminator.</summary>
    /// <remarks>
    ///     Returns <c>null</c> on malformed JSON, missing or unknown <c>$type</c>, or any
    ///     deserialization failure. Callers should log or report errors at their discretion.
    /// </remarks>
    public static OmicronEvent? DeserializeEvent(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("$type"u8, out JsonElement typeEl))
            {
                return null;
            }

            string? typeName = typeEl.GetString();
            if (typeName is null)
            {
                return null;
            }

            Type? type = OmicronEventRegistry.GetType(typeName);
            if (type is null)
            {
                return null;
            }

            return (OmicronEvent?)JsonSerializer.Deserialize(json.Span, type, Default);
        }
        catch
        {
            return null;
        }
    }
}
