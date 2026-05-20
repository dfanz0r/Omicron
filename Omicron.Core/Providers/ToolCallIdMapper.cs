using System.Text.RegularExpressions;
using Omicron.Core.Models;

namespace Omicron.Core.Providers;

/// <summary>
///     Maps between logical tool call IDs (used internally by Omicron)
///     and wire-level IDs (used by provider API requests/responses).
///     Different providers have different constraints on tool call IDs:
///     - OpenAI Chat: arbitrary strings, typically prefixed with "call_"
///     - OpenAI Responses: item IDs (e.g., "item_xxx")
///     - Anthropic: tool_use prefixed IDs
///     - Some providers: length limits, character restrictions
///     This utility ensures cross-API replay works correctly by preserving
///     logical-to-wire mappings in provider metadata.
/// </summary>
public interface IToolCallIdMapper
{
    /// <summary>
    ///     Convert a logical (internal) tool call ID to a wire-level ID
    ///     suitable for the given target API.
    /// </summary>
    string ToWireId(string logicalId, ApiType targetApi, ProviderCompatibility compat);

    /// <summary>
    ///     Convert a wire-level tool call ID back to a logical ID.
    /// </summary>
    string ToLogicalId(string wireId, ApiType sourceApi, ProviderCompatibility compat);

    /// <summary>
    ///     Generate a new logical tool call ID.
    /// </summary>
    string NewLogicalId();
}

/// <summary>
///     Default implementation of IToolCallIdMapper.
/// </summary>
public sealed class ToolCallIdMapper : IToolCallIdMapper
{
    /// <summary>
    ///     Maximum length for tool call IDs (safe default for most providers).
    /// </summary>
    private const int MaxIdLength = 64;

    private static readonly Regex NonAlphanumeric = new(@"[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    public string ToWireId(string logicalId, ApiType targetApi, ProviderCompatibility compat)
    {
        if (string.IsNullOrEmpty(logicalId))
        {
            return NewLogicalId();
        }

        string wireId = logicalId;

        // Sanitize: remove characters that are problematic for any provider
        wireId = NonAlphanumeric.Replace(wireId, "_");

        // Truncate if needed
        int maxLen = targetApi switch
        {
            ApiType.AnthropicMessages => 64,
            ApiType.OpenAiResponses => 64,
            _ => MaxIdLength
        };

        if (wireId.Length > maxLen)
        {
            wireId = wireId[..maxLen];
        }

        return wireId;
    }

    public string ToLogicalId(string wireId, ApiType sourceApi, ProviderCompatibility compat)
    {
        // Wire IDs from providers are already suitable as logical IDs
        // after sanitization
        return wireId ?? NewLogicalId();
    }

    public string NewLogicalId()
    {
        // Use a short, URL-safe unique ID
        return $"call_{Guid.NewGuid().ToString("N")[..12]}";
    }
}
