using Omicron.Core.Commands;
using Omicron.Core.Tools;

namespace Omicron.Core.Extensions;

/// <summary>
///     Default in-memory extension registry.
///     When an extension is registered, it receives an <see cref="IExtensionContext" />
///     that forwards its contributions to the appropriate registries.
/// </summary>
public sealed class ExtensionRegistry : IExtensionRegistry
{
    private readonly ICommandRegistry _commandRegistry;
    private readonly List<ExtensionMetadata> _extensions = [];
    private readonly IToolRegistry _toolRegistry;

    public ExtensionRegistry(IToolRegistry toolRegistry, ICommandRegistry commandRegistry)
    {
        _toolRegistry = toolRegistry;
        _commandRegistry = commandRegistry;
    }

    public IReadOnlyList<ExtensionMetadata> Extensions => _extensions.AsReadOnly();

    public void Register(IOmicronExtension extension)
    {
        var context = new ExtensionContext(_toolRegistry, _commandRegistry);
        extension.Register(context);

        _extensions.Add(new ExtensionMetadata(extension.Id, extension.DisplayName, extension.Version));
    }

    /// <summary>
    ///     Context implementation that forwards contributions to the host registries.
    /// </summary>
    private sealed class ExtensionContext : IExtensionContext
    {
        private readonly ICommandRegistry _commandRegistry;
        private readonly IToolRegistry _toolRegistry;

        public ExtensionContext(IToolRegistry toolRegistry, ICommandRegistry commandRegistry)
        {
            _toolRegistry = toolRegistry;
            _commandRegistry = commandRegistry;
        }

        public void RegisterTool(ToolDefinition tool)
        {
            _toolRegistry.Register(tool);
        }

        public void RegisterCommand(CommandDefinition command)
        {
            _commandRegistry.Register(command);
        }
    }
}
