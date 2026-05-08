namespace Omicron.Core.Commands;

/// <summary>
/// Scope where a command is valid.
/// </summary>
public enum CommandScope
{
    /// <summary>Available globally at any time.</summary>
    Global,
    /// <summary>Only available during an active session/chat.</summary>
    Session,
    /// <summary>Only available from the configuration/view.</summary>
    Config
}

/// <summary>
/// Context provided when a command is executed.
/// </summary>
public sealed record CommandContext(
    string Arguments,
    CancellationToken CancellationToken);

/// <summary>
/// Result of executing a command.
/// </summary>
public sealed record CommandResult(string Output, bool IsError = false);

/// <summary>
/// Defines a user-invokable command.
/// </summary>
public sealed record CommandDefinition(
    string Id,
    string Title,
    string? Description,
    CommandScope Scope,
    Func<CommandContext, Task<CommandResult>> ExecuteAsync);

/// <summary>
/// Registry of frontend/plugin commands.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>
    /// Register a command definition.
    /// </summary>
    void Register(CommandDefinition command);

    /// <summary>
    /// Get a command by ID. Returns null if not found.
    /// </summary>
    CommandDefinition? GetCommand(string id);

    /// <summary>
    /// All registered commands.
    /// </summary>
    IReadOnlyList<CommandDefinition> AllCommands { get; }
}

/// <summary>
/// Default in-memory command registry.
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.OrdinalIgnoreCase);

    public void Register(CommandDefinition command)
    {
        _commands[command.Id] = command;
    }

    public CommandDefinition? GetCommand(string id)
    {
        _commands.TryGetValue(id, out var command);
        return command;
    }

    public IReadOnlyList<CommandDefinition> AllCommands => _commands.Values.ToList();
}
