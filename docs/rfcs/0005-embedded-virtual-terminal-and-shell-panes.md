# RFC 0005: Embedded Virtual Terminal and Shell Panes

Status: **Planned**

## Purpose

Define Omicron's built-in virtual terminal emulator and tmux-like pane system. This feature lets users do manual shell work inside Omicron while still benefiting from AI command assistance, session history, workspace context, snapshots, and future sandboxing.

## Core Requirement

The virtual terminal is distinct from the host terminal used to display the TUI.

> A virtual terminal pane must not write child-process ANSI directly to the host terminal. Child output flows through a PTY/process adapter into Omicron's terminal emulator buffer, then the TUI or GUI renders that buffer.

## Target Capabilities

- Spawn one or more shell sessions from Omicron, including shells owned by remote backend agents.
- Arrange shells as tmux-like panes, tabs, or splits.
- Support normal manual developer workflow.
- Let the assistant propose, compose, explain, edit, or insert commands.
- Require explicit approval before AI-generated commands are executed by default.
- Capture selected terminal output as session context.
- Associate shell activity with workspace snapshots and session history.
- Optionally run shell panes under sandbox providers later.

## Terminal Emulation Pipeline

```text
child shell/process
  ↓
pseudo-terminal adapter
  ↓
byte stream
  ↓
VT/ANSI parser
  ↓
emulated screen buffer + scrollback
  ↓
terminal pane model
  ↓
TUI frame buffer, GUI terminal control, or remoting terminal channel
```

## Core Concepts

```csharp
public sealed record ShellSessionId(Guid Value);
public sealed record TerminalPaneId(Guid Value);

public sealed record ShellSessionSpec(
    string ShellExecutable,
    IReadOnlyList<string> Args,
    WorkspacePath? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public interface IVirtualTerminalSession : IAsyncDisposable
{
    ShellSessionId Id { get; }
    TerminalSize Size { get; }
    TerminalScreenSnapshot Screen { get; }

    ValueTask ResizeAsync(TerminalSize size, CancellationToken ct);
    ValueTask SendInputAsync(ReadOnlyMemory<byte> utf8OrControlBytes, CancellationToken ct);
    IAsyncEnumerable<VirtualTerminalEvent> ReadEventsAsync(CancellationToken ct);
}
```

## Emulator Requirements

Minimum required support:

- cursor movement and erasing
- SGR styles/colors
- alternate screen mode
- line wrapping and resizing
- scroll regions
- OSC title updates where useful
- bracketed paste
- basic mouse reporting later if needed

The emulator should maintain:

- visible screen buffer
- scrollback buffer
- cursor state
- style state
- title/mode metadata
- child process connection status

## Pane Orchestration

Omicron models layouts with frontend-neutral state.

```text
ShellWorkspace
  tabs
    split tree
      terminal pane
      transcript pane
      tool/log pane
```

Pane operations are commands:

```text
terminal.newPane
terminal.closePane
terminal.splitHorizontal
terminal.splitVertical
terminal.focusNextPane
terminal.resizePane
terminal.sendInput
terminal.captureSelectionToPrompt
terminal.attachOutputToSession
terminal.aiSuggestCommand
terminal.aiInsertCommand
```

The TUI renders panes inside the alternate-screen app. The GUI renders the same shell sessions with native tabs, splitters, and terminal widgets. Remote terminal panes are addressed by backend/session/agent/terminal ids and streamed through RFC 0014 channels.

## AI-Assisted Command Writing

AI command assistance is user-mediated by default.

Modes:

```text
suggest      assistant proposes command text with explanation
insert       assistant inserts command into focused terminal input, not executed
edit         assistant rewrites selected/current command line
explain      assistant explains selected command/output
execute      assistant sends command only after policy/user approval
```

Safety rule:

> AI-generated shell commands should not be sent to a live terminal pane silently. Default behavior is propose/insert, with explicit execution approval based on risk and user settings.

The command-composition layer should understand:

- focused terminal pane
- owning backend/session/agent when remote
- current working directory
- workspace VFS/snapshot state
- selected/recent terminal output
- shell dialect: PowerShell, cmd, bash, zsh, fish, etc.
- whether a command mutates files, installs packages, starts servers, uses network, or is destructive

## Persistence

Persist carefully:

- shell session metadata
- pane layout
- terminal scrollback/output when configured or explicitly attached
- AI command suggestions
- approvals/rejections
- sent command text
- links to workspace snapshots before/after command batches when useful

Do not assume live process state is snapshot-safe. Omicron can restore pane layout and scrollback, but restoring a running process exactly is not practical without deeper OS/container support.

Crash recovery should:

```text
restore pane layout
restore scrollback/output history
show process as disconnected if gone
allow user to restart shell in same working directory
link restarted shell to same Omicron session
```

## Sandboxing Relationship

Virtual terminal panes can run in two modes:

```text
normal pane:
  host shell with visible unsafe/local indication where appropriate

sandboxed pane:
  shell runs under sandbox provider with visible policy badges
```

Sandboxed panes may be limited by provider support for PTYs, interactive processes, networking, and filesystem mounts.

## Design Decisions

1. Virtual terminal panes use a real emulator, not raw passthrough.
2. Pane layout is semantic state, not frontend-only state.
3. AI command writing is propose/insert by default.
4. Live process state is not part of session snapshot guarantees.
5. Terminal panes integrate with session history, VFS, sandboxing, and remoting but do not replace those systems.
6. Remote terminal streams use RFC 0014 channel priority and flow control so terminal spam cannot starve cancellation or other agents.
