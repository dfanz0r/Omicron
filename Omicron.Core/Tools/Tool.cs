using System.Text.Json;

namespace Omicron.Core;

/// <summary>
/// Defines a tool/function the LLM can call.
/// </summary>
public class Tool
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public JsonElement? Parameters { get; init; }

    /// <summary>
    /// Executes this tool with the given JSON arguments.
    /// Returns a result text to send back to the LLM.
    /// </summary>
    public Func<string, Dictionary<string, object?>?, Task<string>>? ExecuteAsync { get; set; }
}

/// <summary>
/// Helper for building tool definitions from JSON schema.
/// </summary>
public static class ToolSchema
{
    public static JsonElement FromString(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement Object(Dictionary<string, JsonElement> properties, string[]? required = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        foreach (var (name, schema) in properties)
        {
            writer.WritePropertyName(name);
            schema.WriteTo(writer);
        }
        writer.WriteEndObject();

        if (required?.Length > 0)
        {
            writer.WriteStartArray("required");
            foreach (var r in required) writer.WriteStringValue(r);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.Flush();

        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    public static JsonElement StringProperty(string description) =>
        JsonDocument.Parse($$"""{"type":"string","description":"{{description}}"}""").RootElement.Clone();

    public static JsonElement IntegerProperty(string description) =>
        JsonDocument.Parse($$"""{"type":"integer","description":"{{description}}"}""").RootElement.Clone();

    public static JsonElement BooleanProperty(string description) =>
        JsonDocument.Parse($$"""{"type":"boolean","description":"{{description}}"}""").RootElement.Clone();

    public static JsonElement ArrayProperty(string description, JsonElement items) =>
        JsonDocument.Parse($$"""{"type":"array","description":"{{description}}","items":{{items.GetRawText()}}}""").RootElement.Clone();

    public static JsonElement EnumProperty(string description, string[] values)
    {
        var items = string.Join(",", values.Select(v => $"\"{v}\""));
        return JsonDocument.Parse($$"""{"type":"string","description":"{{description}}","enum":[{{items}}]}""").RootElement.Clone();
    }
}
