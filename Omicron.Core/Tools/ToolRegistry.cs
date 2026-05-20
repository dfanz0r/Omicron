using System.Text;
using System.Text.Json;
using Omicron.Core.Content;
using Omicron.Core.Events;
using Omicron.Core.Models;
using Omicron.Core.Text;

namespace Omicron.Core.Tools;

/// <summary>
///     Describes a tool that an LLM can call. Used for tool registration
///     and JSON-schema generation.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement? Parameters,
    Func<ToolInvocationContext, Task<ToolResult>> InvokeAsync);

/// <summary>
///     Context provided to a tool when it is invoked by the agent loop.
/// </summary>
public sealed record ToolInvocationContext(
    ToolCallId ToolCallId,
    IReadOnlyDictionary<string, object?> Arguments,
    SessionId SessionId,
    AgentId AgentId,
    CancellationToken CancellationToken,
    ModelMetadata? ModelMetadata = null);

/// <summary>
///     Result returned by a tool after execution.
///     Text is stored as owned UTF-8 via <see cref="Utf8String" />.
///     Use <see cref="TextData" /> for zero-alloc span access, or
///     <see cref="GetText()" /> for explicit string materialization.
/// </summary>
/// <param name="TextData">The result text as owned UTF-8. Null when no text output.</param>
/// <param name="IsError">Whether the tool execution failed.</param>
/// <param name="Blocks">Rich content blocks with native UTF-8 data.</param>
public sealed record ToolResult(
    Utf8String? TextData = null,
    bool IsError = false,
    List<IContentBlock>? Blocks = null)
{
    /// <summary>
    ///     Get the result as a string. Prefers <see cref="Blocks" />, then <see cref="TextData" />.
    ///     This is an explicit string materialization; prefer <see cref="TextData" />.Utf8Span
    ///     for zero-alloc UTF-8 access.
    /// </summary>
    public string GetText()
    {
        if (Blocks is { Count: > 0 })
        {
            var sb = new Utf8Builder();
            try
            {
                ContentBlockTextRenderer.AppendAllUtf8To(ref sb, Blocks);
                return sb.ToString();
            }
            finally
            {
                sb.Dispose();
            }
        }

        return TextData?.ToString() ?? string.Empty;
    }
}

/// <summary>
///     Registry of all tools available to the LLM agent.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    ///     All registered tools.
    /// </summary>
    IReadOnlyList<ToolDefinition> AllTools { get; }

    /// <summary>
    ///     Register a tool definition.
    /// </summary>
    void Register(ToolDefinition tool);

    /// <summary>
    ///     Get a tool by name. Returns null if not found.
    /// </summary>
    ToolDefinition? GetTool(string name);

    /// <summary>
    ///     Check if a tool with the given name is registered.
    /// </summary>
    bool HasTool(string name);
}

/// <summary>
///     Default in-memory tool registry.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ToolDefinition tool)
    {
        _tools[tool.Name] = tool;
    }

    public ToolDefinition? GetTool(string name)
    {
        _tools.TryGetValue(name, out ToolDefinition? tool);
        return tool;
    }

    public IReadOnlyList<ToolDefinition> AllTools => _tools.Values.ToList();

    public bool HasTool(string name)
    {
        return _tools.ContainsKey(name);
    }
}
