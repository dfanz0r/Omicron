using Omicron.Core.Commands;
using Omicron.Core.Events;
using Omicron.Core.Permissions;
using Omicron.Core.Tools;
using Omicron.Core.Workspace;
using Xunit;

namespace Omicron.Core.Tests;

public class ToolRegistryTests
{
    [Fact]
    public void ToolRegistry_RegisterAndGet()
    {
        var registry = new ToolRegistry();
        var tool = new ToolDefinition("calculator",
            "Do math",
            null,
            ctx => Task.FromResult(new ToolResult("42")));

        registry.Register(tool);
        Assert.True(registry.HasTool("calculator"));
        Assert.NotNull(registry.GetTool("calculator"));
        Assert.Single(registry.AllTools);
    }

    [Fact]
    public void ToolRegistry_GetTool_ReturnsNullForUnknown()
    {
        var registry = new ToolRegistry();
        Assert.Null(registry.GetTool("nonexistent"));
    }

    [Fact]
    public void ToolRegistry_Register_OverwritesExisting()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition("calc", "v1", null, ctx => Task.FromResult(new ToolResult("1"))));
        registry.Register(new ToolDefinition("calc", "v2", null, ctx => Task.FromResult(new ToolResult("2"))));

        Assert.Single(registry.AllTools);
        Assert.Equal("v2", registry.GetTool("calc")!.Description);
    }

    [Fact]
    public async Task ToolRegistry_ToolInvocation_ReturnsResult()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition("echo",
            "Echo input",
            null,
            ctx =>
                Task.FromResult(new ToolResult(ctx.Arguments.GetValueOrDefault("msg")?.ToString() ?? ""))));

        ToolDefinition? tool = registry.GetTool("echo");
        Assert.NotNull(tool);

        ToolResult result = await tool.InvokeAsync(new ToolInvocationContext(new ToolCallId("call_1"),
            new Dictionary<string, object?>
            {
                ["msg"] = "Hello"
            },
            SessionId.New(),
            AgentId.New(),
            CancellationToken.None));

        Assert.Equal("Hello", result.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public void ToolInvocationContext_StoresAllFields()
    {
        var sessionId = SessionId.New();
        var agentId = AgentId.New();
        var args = new Dictionary<string, object?>
        {
            ["key"] = "value"
        };

        var ctx = new ToolInvocationContext(new ToolCallId("call_1"),
            args,
            sessionId,
            agentId,
            CancellationToken.None);

        Assert.Equal("call_1", ctx.ToolCallId.Value);
        Assert.Equal("value", ctx.Arguments["key"]);
        Assert.Equal(sessionId, ctx.SessionId);
        Assert.Equal(agentId, ctx.AgentId);
    }
}

public class CommandRegistryTests
{
    [Fact]
    public void CommandRegistry_RegisterAndGet()
    {
        var registry = new CommandRegistry();
        var cmd = new CommandDefinition("session.reset",
            "Reset Session",
            "Clear conversation history",
            CommandScope.Session,
            ctx => Task.FromResult(new CommandResult("Reset")));

        registry.Register(cmd);
        Assert.NotNull(registry.GetCommand("session.reset"));
        Assert.Single(registry.AllCommands);
    }

    [Fact]
    public void CommandRegistry_GetCommand_ReturnsNullForUnknown()
    {
        var registry = new CommandRegistry();
        Assert.Null(registry.GetCommand("nonexistent"));
    }
}

public class PermissionTests
{
    [Fact]
    public async Task AllowAllPermissionService_AllowsEverything()
    {
        var service = new AllowAllPermissionService();
        PermissionDecision result = await service.RequestAsync(new PermissionRequest("tool.execute", "shell", null));
        Assert.True(result.Allowed);
    }
}

public class WorkspaceTests
{
    [Fact]
    public void HostWorkspace_Constructor_SetsRoot()
    {
        var vfs = new HostWorkspaceFileSystem(Environment.CurrentDirectory);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), vfs.RootPath);
    }
}
