using System.Text.Json;
using Omicron.Core.Events;
using Omicron.Core.Sessions;

namespace Omicron.Core.Tools;

/// <summary>
/// Describes a tool that an LLM can call. Used for tool registration
/// and JSON-schema generation.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement? Parameters,
    Func<ToolInvocationContext, Task<ToolResult>> InvokeAsync);

/// <summary>
/// Context provided to a tool when it is invoked by the agent loop.
/// </summary>
public sealed record ToolInvocationContext(
    ToolCallId ToolCallId,
    IReadOnlyDictionary<string, object?> Arguments,
    SessionId SessionId,
    AgentId AgentId,
    CancellationToken CancellationToken);

/// <summary>
/// Result returned by a tool after execution.
/// </summary>
public sealed record ToolResult(string Text, bool IsError = false);

/// <summary>
/// Registry of all tools available to the LLM agent.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Register a tool definition.
    /// </summary>
    void Register(ToolDefinition tool);

    /// <summary>
    /// Get a tool by name. Returns null if not found.
    /// </summary>
    ToolDefinition? GetTool(string name);

    /// <summary>
    /// All registered tools.
    /// </summary>
    IReadOnlyList<ToolDefinition> AllTools { get; }

    /// <summary>
    /// Check if a tool with the given name is registered.
    /// </summary>
    bool HasTool(string name);
}

/// <summary>
/// Default in-memory tool registry.
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
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public IReadOnlyList<ToolDefinition> AllTools => _tools.Values.ToList();

    public bool HasTool(string name) => _tools.ContainsKey(name);
}
