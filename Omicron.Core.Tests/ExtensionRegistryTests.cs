using System.Text.Json;
using Omicron.Core.Commands;
using Omicron.Core.Extensions;
using Omicron.Core.Models;
using Omicron.Core.Tools;
using Xunit;

namespace Omicron.Core.Tests;

public class ExtensionRegistryTests
{
    [Fact]
    public void ExtensionRegistry_RegisterExtension_AddsMetadata()
    {
        var toolRegistry = new ToolRegistry();
        var cmdRegistry = new CommandRegistry();
        var extRegistry = new ExtensionRegistry(toolRegistry, cmdRegistry);

        var ext = new TestExtension();
        extRegistry.Register(ext);

        Assert.Single(extRegistry.Extensions);
        Assert.Equal("test.extension", extRegistry.Extensions[0].Id);
    }

    [Fact]
    public void ExtensionRegistry_ExtensionCanRegisterTools()
    {
        var toolRegistry = new ToolRegistry();
        var cmdRegistry = new CommandRegistry();
        var extRegistry = new ExtensionRegistry(toolRegistry, cmdRegistry);

        extRegistry.Register(new ToolRegisteringExtension());

        Assert.True(toolRegistry.HasTool("test_tool"));
    }

    [Fact]
    public void ExtensionRegistry_ExtensionCanRegisterCommands()
    {
        var toolRegistry = new ToolRegistry();
        var cmdRegistry = new CommandRegistry();
        var extRegistry = new ExtensionRegistry(toolRegistry, cmdRegistry);

        extRegistry.Register(new CommandRegisteringExtension());

        Assert.NotNull(cmdRegistry.GetCommand("test.command"));
    }
}

public class OmicronHostTests
{
    [Fact]
    public void OmicronHost_Constructor_CreatesAllServices()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);

        Assert.NotNull(host.Extensions);
        Assert.NotNull(host.Providers);
        Assert.NotNull(host.Tools);
        Assert.NotNull(host.Commands);
        Assert.NotNull(host.Permissions);
        Assert.NotNull(host.Workspace);
        Assert.NotNull(host.Execution);
        Assert.NotNull(host.Events);
        Assert.NotNull(host.EventLog);
        Assert.NotNull(host.ProviderState);
        Assert.NotNull(host.ProviderStateManager);
    }

    [Fact]
    public void OmicronHost_LoadBuiltinExtensions_RegistersCalculatorAndTime()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        host.LoadBuiltinExtensions();

        Assert.True(host.Tools.HasTool("calculator"));
        Assert.True(host.Tools.HasTool("get_current_time"));
    }

    [Fact]
    public void OmicronHost_CreateSession_ReturnsAgentSession()
    {
        var host = new OmicronHost(Environment.CurrentDirectory);
        var model = new Model
        {
            Id = "test",
            Name = "Test",
            ProviderName = "test"
        };

        var session = host.CreateSession(model, "You are a test.");

        Assert.NotNull(session);
        Assert.Equal(model, session.Model);
        Assert.Equal("You are a test.", session.SystemPrompt);
    }
}

// ================================================================
// Test extensions used by ExtensionRegistry tests
// ================================================================

public class TestExtension : IOmicronExtension
{
    public string Id => "test.extension";
    public string DisplayName => "Test Extension";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context) { }
}

public class ToolRegisteringExtension : IOmicronExtension
{
    public string Id => "test.tool-extension";
    public string DisplayName => "Tool Test Extension";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        context.RegisterTool(new ToolDefinition(
            "test_tool", "A test tool", null,
            ctx => Task.FromResult(new ToolResult("done"))));
    }
}

public class CommandRegisteringExtension : IOmicronExtension
{
    public string Id => "test.command-extension";
    public string DisplayName => "Command Test Extension";
    public Version Version => new(1, 0, 0);

    public void Register(IExtensionContext context)
    {
        context.RegisterCommand(new CommandDefinition(
            "test.command", "Test Command", "A test command",
            CommandScope.Global,
            ctx => Task.FromResult(new CommandResult("done"))));
    }
}
