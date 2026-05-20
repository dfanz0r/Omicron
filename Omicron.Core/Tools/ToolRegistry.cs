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
/// </summary>
/// <param name="Text">Error/small text output. Not set when <see cref="Utf8Data" /> is provided.</param>
/// <param name="Utf8Data">Primary output as UTF-8 bytes. Preferred over <c>Text</c> for large responses.</param>
/// <param name="IsError">Whether the tool execution failed.</param>
/// <param name="Blocks">Rich content blocks with native UTF-8 data.</param>
public sealed record ToolResult(
    string? Text = null,
    ReadOnlyMemory<byte>? Utf8Data = null,
    bool IsError = false,
    List<IContentBlock>? Blocks = null)
{
    /// <summary>
    ///     Get the result as a string. Prefers <see cref="Blocks" />, then <see cref="Utf8Data" />, then
    ///     <see cref="Text" />.
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

        if (Utf8Data.HasValue)
        {
            return Encoding.UTF8.GetString(Utf8Data.Value.Span);
        }

        return Text ?? string.Empty;
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
