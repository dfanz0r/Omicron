# RFC 0002: UI Abstractions, Plugins, Commands, and Permissions

Status: **Canonical**

## Purpose

Define the frontend-neutral extension and interaction model for Omicron. Plugins should contribute capabilities and semantic UI without depending on terminal or GUI rendering systems.

This RFC defines the logical plugin API. The preferred execution format for third-party plugins is WebAssembly with capability-based host bindings, described in RFC 0010. Remote backend and multi-agent routing implications are defined in RFC 0014.

## Current Implementation Snapshot

As of 2026-05-08, this RFC is mostly target architecture. The codebase has a simple in-process `Tool` contract (`Name`, `Description`, JSON-schema `Parameters`, async `ExecuteAsync`) and the CLI registers built-in calculator, time, `read_path`, and `shell` tools directly on the `Agent`. There is not yet a command system, permission prompt model, plugin manifest/capability model, semantic UI model, panel/status providers, or WASM plugin host. Direct file/shell tools should be treated as MVP conveniences to be moved behind this RFC's permission and capability boundaries.

## Plugin Categories

### Headless Plugins

Headless plugins work everywhere and expose behavior rather than frontend-specific rendering.

Examples:

- git tools
- file search
- repo indexing
- linters and formatters
- test runners
- model providers
- memory/session stores
- language server adapters

```csharp
public interface IAgentPlugin
{
    void Configure(IPluginHost host);
}

public interface IPluginHost
{
    void RegisterTool(ITool tool);
    void RegisterCommand(ICommand command);
    void RegisterModelProvider(IModelProvider provider);
    void RegisterPanel(IPanelProvider panelProvider);
    void RegisterStatusItem(IStatusItemProvider statusItemProvider);
    void Publish(AgentEvent evt);
}
```

For built-in or trusted-native plugins this may be a direct .NET interface. For third-party plugins, the same logical operations should be exposed as WASM host bindings rather than direct in-process object access.

Provider plugins should be able to register or override model providers, but credentials and secret access must remain host-mediated.

### Semantic UI Plugins

Semantic UI plugins contribute frontend-neutral UI nodes.

```csharp
public interface IPanelProvider
{
    string Id { get; }
    string Title { get; }
    UiNode Build(UiContext context);
}
```

Examples:

- test result panel
- git diff panel
- tool-call inspector
- token usage panel
- project status panel
- settings page

### Frontend-Specific Plugins

Some integrations may legitimately require terminal or GUI APIs.

Examples:

- terminal shell integration
- GUI image previewer
- GUI webview
- native file picker
- platform notifications

These must be explicitly marked and must not become dependencies of general plugins.

```csharp
public interface ITuiPlugin { }
public interface IGuiPlugin { }
```

## Plugin Execution Tiers

Omicron should distinguish plugin API shape from plugin execution trust level.

```text
builtin:
  compiled with Omicron, full internal API when necessary

trusted-native:
  local development or enterprise-only .NET/native plugins, explicit unsafe mode

wasm:
  default third-party plugin format, capability-limited through host bindings

frontend-specific:
  explicitly marked TUI/GUI plugins with additional review
```

Third-party plugins should not receive ambient filesystem, network, process, credential, terminal, GUI, or remoting-transport access. They should call Omicron host APIs, which enforce permissions, VFS boundaries, sandbox broker rules, semantic UI constraints, and remote target authorization.

See RFC 0010 for the WebAssembly plugin runtime and RFC 0014 for remote backend transport and target addressing.

## Plugin Placement and Remote Backends

Remoting makes plugin placement explicit. A plugin can run in one of several places:

```text
backend plugin:
  runs on the backend host; may access backend-mediated workspace/tools for that host

frontend plugin:
  runs in a specific frontend; may contribute local UI affordances only

coordinating plugin:
  registers commands/tools that can target one or more backends through host-mediated APIs
```

General third-party plugins should not open sockets, SSH sessions, QUIC streams, or relay connections directly. Remote execution is requested through Omicron host APIs that require explicit target ids and capabilities.

Plugin APIs that operate on runtime state should carry explicit scope:

```text
backendId?
sessionId?
agentId?
taskId?
operationId?
terminalId?
workspaceId?
```

Plugins must not rely on the frontend's currently selected agent as implicit authority. A command button in a panel may be pre-bound to an `agentId`, but the command invocation still carries that target id in structured args.

Plugins that subscribe to events should declare both event types and scope. A plugin may request dashboard-level summaries, per-session events, or per-agent streams. High-volume subscriptions must respect RFC 0014 attention modes and backpressure rules.

## Semantic UI Model

`Omicron.UI.Abstractions` is not a general-purpose UI toolkit. It is a compact semantic model for common agent surfaces.

Good primitives:

```text
Text
Markdown
Code
Diff
Table
Tree
Form
Button / Command
Progress
LogStream
TerminalPaneRef
AgentRef
AgentList
Panel
Notification
```

Avoid exposing:

```text
Terminal cells
ANSI codes
GUI control classes
Font objects
Pixel geometry
Platform-specific keyboard types
```

Representative `UiNode` model:

```csharp
public abstract record UiNode;

public sealed record VStack(
    IReadOnlyList<UiNode> Children,
    UiLayoutOptions? Layout = null) : UiNode;

public sealed record HStack(
    IReadOnlyList<UiNode> Children,
    UiLayoutOptions? Layout = null) : UiNode;

public sealed record TextBlock(UiText Text) : UiNode;

public sealed record MarkdownBlock(
    ReadOnlyMemory<byte> Utf8Markdown) : UiNode;

public sealed record CodeBlock(
    string Language,
    ReadOnlyMemory<byte> Utf8Code) : UiNode;

public sealed record DiffBlock(DiffModel Diff) : UiNode;

public sealed record TableBlock(
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<TableRow> Rows) : UiNode;

public sealed record TreeBlock(
    IReadOnlyList<TreeNode> Roots) : UiNode;

public sealed record ProgressBlock(
    double? Value,
    UiText? Label) : UiNode;

public sealed record ButtonBlock(
    string Id,
    UiText Label,
    CommandRef Command) : UiNode;

public sealed record FormBlock(
    string Id,
    IReadOnlyList<FormField> Fields,
    CommandRef SubmitCommand) : UiNode;

public sealed record TerminalPaneBlock(
    TerminalPaneId PaneId,
    TerminalPanePresentationOptions Options) : UiNode;

public sealed record AgentRefBlock(
    AgentId AgentId,
    UiText Label,
    AgentPresentationOptions Options,
    CommandRef? ActivateCommand = null) : UiNode;

public sealed record AgentListBlock(
    IReadOnlyList<AgentRefBlock> Agents,
    AgentListPresentationOptions Options) : UiNode;
```

## Content Blocks

Messages and tool results should be structured, not just raw strings.

```csharp
public abstract record ContentBlock;

public sealed record PlainTextContentBlock(
    ReadOnlyMemory<byte> Utf8) : ContentBlock;

public sealed record MarkdownContentBlock(
    ReadOnlyMemory<byte> Utf8Markdown) : ContentBlock;

public sealed record CodeContentBlock(
    string Language,
    ReadOnlyMemory<byte> Utf8Code) : ContentBlock;

public sealed record DiffContentBlock(
    DiffModel Diff) : ContentBlock;

public sealed record ToolResultContentBlock(
    string ToolName,
    JsonElement Data) : ContentBlock;

public sealed record DiagnosticContentBlock(
    IReadOnlyList<DiagnosticItem> Diagnostics) : ContentBlock;

public sealed record TerminalOutputContentBlock(
    ShellSessionId ShellSessionId,
    ReadOnlyMemory<byte> Utf8PlainTextOutput,
    TerminalOutputKind Kind) : ContentBlock;
```

The terminal frontend renders these as styled cells. The GUI renders them as rich controls.

## Command System

Commands are semantic and shared across frontends.

```csharp
public sealed record CommandRef(string Id, JsonElement? Args = null);

public interface ICommand
{
    string Id { get; }
    string Title { get; }
    string? Description { get; }
    ValueTask ExecuteAsync(CommandContext context, JsonElement? args, CancellationToken ct);
}
```

Canonical examples:

```text
omicron.cancel
omicron.retry
omicron.newSession
omicron.openSettings
transcript.scrollToBottom
transcript.copyLastMessage
terminal.newPane
terminal.splitHorizontal
terminal.splitVertical
terminal.sendInput
terminal.aiSuggestCommand
terminal.aiInsertCommand
agent.foreground
agent.setAttention
agent.pause
agent.resume
agent.cancel
agent.openInSplit
session.listAgents
sandbox.explainPolicy
sandbox.approveExecution
sandbox.rejectExecution
tool.approve
tool.reject
git.showDiff
tests.run
```

TUI maps keybindings and slash commands to these IDs. GUI maps menu items, buttons, toolbar actions, and shortcuts to the same IDs.

## Permission Model

Coding agents need a clear permission flow.

```csharp
public sealed record PermissionRequest(
    PermissionRequestId Id,
    string Title,
    string Description,
    IReadOnlyList<PermissionOption> Options,
    PermissionRisk Risk,
    PermissionTarget? Target,
    JsonElement? Details);

public sealed record PermissionTarget(
    BackendId? BackendId,
    SessionId? SessionId,
    AgentId? AgentId,
    WorkspaceId? WorkspaceId,
    TerminalPaneId? TerminalPaneId);
```

Frontends choose presentation:

```text
TUI:
  modal overlay or inline confirmation block

GUI:
  dialog, side panel, or notification card
```

Tools, plugins, shell panes, remote agents, and sandbox execution requests must request permission through the core. They must not directly prompt through a frontend. Permission requests from quiet/background remote agents should use notification severity and delivery hints from RFC 0014 so the user can be alerted without streaming the full agent transcript.

## Design Decisions

1. General plugins depend only on `Omicron.Core` and `Omicron.UI.Abstractions` at the logical API level.
2. Third-party plugins should use WASM host bindings rather than direct in-process references.
3. Semantic UI primitives remain intentionally small.
4. Commands are stable IDs with structured args.
5. Permission requests are core events, not frontend callbacks.
6. Terminal panes can be referenced semantically, but terminal emulation/rendering stays outside `Omicron.UI.Abstractions`.
7. Plugin commands and event subscriptions must carry explicit backend/session/agent target scope when they operate on remote or multi-agent state.
8. General plugins do not own remoting transports; they use host-mediated remote APIs defined by RFC 0014.
