using Omicron.Core.Commands;
using Omicron.Core.Tools;

namespace Omicron.Core.Extensions;

/// <summary>
/// Metadata about a registered extension.
/// </summary>
public sealed record ExtensionMetadata(
    string Id,
    string DisplayName,
    Version Version);

/// <summary>
/// Registry of all loaded extensions. Extensions register tools, commands,
/// providers, and UI components through <see cref="IExtensionContext"/>.
/// </summary>
public interface IExtensionRegistry
{
    /// <summary>
    /// Load an extension by calling its <see cref="IOmicronExtension.Register"/>
    /// method with a context bound to this registry.
    /// </summary>
    void Register(IOmicronExtension extension);

    /// <summary>
    /// All registered extension metadata.
    /// </summary>
    IReadOnlyList<ExtensionMetadata> Extensions { get; }
}

/// <summary>
/// Context provided to an extension during registration. Allows the extension
/// to contribute tools, commands, providers, and UI components to the host.
/// </summary>
public interface IExtensionContext
{
    /// <summary>
    /// Register a tool that the LLM can call.
    /// </summary>
    void RegisterTool(ToolDefinition tool);

    /// <summary>
    /// Register a command that the frontend or CLI can invoke.
    /// </summary>
    void RegisterCommand(CommandDefinition command);
}

/// <summary>
/// Base interface for all Omicron extensions (built-in C# or future WASM plugins).
/// </summary>
public interface IOmicronExtension
{
    /// <summary>Unique identifier for this extension.</summary>
    string Id { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>Extension version.</summary>
    Version Version { get; }

    /// <summary>
    /// Called during host startup. Use <paramref name="context"/> to
    /// register tools, commands, providers, and other contributions.
    /// </summary>
    void Register(IExtensionContext context);
}
