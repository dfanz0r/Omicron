using System.Text.Json;
using Omicron.Core.Content;

namespace Omicron.Core.Events;

/// <summary>
/// Centralized JSON serializer options for Omicron events.
/// All event serialization paths (store, session resume, fork, export)
/// should use <see cref="CreateOptions"/> to ensure consistent converter
/// registration and prevent reflection-serialization of ref-like members.
/// </summary>
internal static class OmicronEventJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Create a new <see cref="JsonSerializerOptions"/> with standard event settings.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            Converters = { new ContentBlockJsonConverter() }
        };
    }

    /// <summary>Get the shared singleton options instance.</summary>
    public static JsonSerializerOptions Default => Options;
}
