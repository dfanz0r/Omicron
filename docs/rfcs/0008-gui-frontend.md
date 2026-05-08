# RFC 0008: GUI Frontend

Status: **Planned**

## Purpose

Define the GUI frontend direction. The GUI consumes the same Omicron core event stream and semantic UI model as the TUI, but it does not reuse the terminal renderer. The GUI should also be able to act as a remote/multi-agent frontend over the remoting boundary defined in RFC 0014.

## Frontend Boundary

The GUI owns:

- native/cross-platform controls
- rich text rendering
- virtualized lists
- windows/panels
- mouse/keyboard routing
- accessibility
- native dialogs
- virtual terminal pane widgets
- platform clipboard and selection behavior
- font shaping and pixel layout

The GUI shares:

- session model
- backend/session/agent inventory model
- tool-call model
- content blocks
- semantic UI nodes
- command system
- permission model
- plugin panels
- persistence/session history
- workspace snapshots
- virtual terminal pane model
- sandbox execution events/policies
- remoting target/subscription/attention model

## Candidate Frameworks

Potential GUI frameworks:

- Avalonia
- WPF
- WinUI
- MAUI
- Eto.Forms
- future web frontend

For cross-platform desktop, Avalonia is the most natural initial candidate.

## Rendering Path

```text
Omicron event stream or remoting event subscription
  ↓
Backend/session/agent view models
  ↓
Semantic content blocks and terminal pane models
  ↓
Virtualized GUI controls
  ↓
Native/rich rendering
```

The GUI should rely on platform/toolkit support for:

- font shaping
- Unicode rendering
- selection
- accessibility
- scrollbars
- clipboard
- rich text
- image previews
- terminal-pane rendering surfaces where appropriate

## Transcript View

The GUI transcript should be virtualized.

Requirements:

- handle long sessions without rendering every block
- preserve streaming updates
- support selection/copy
- support rich code and diff blocks
- support collapsed tool calls
- support search
- support jump-to-event/snapshot
- support per-agent transcripts and foreground/background/quiet transitions

The GUI may use toolkit text primitives but should still consume the shared block/index model.

## Multi-Agent and Remote Dashboard

The GUI is a natural host for a fleet/multi-agent dashboard. It should support:

- multiple backend connections
- active session and agent inventory
- foreground, visible, background, quiet, and hidden agent attention modes
- tmux-style split views for multiple agents
- notification escalation from quiet/background agents
- backfill when an agent is foregrounded
- per-agent cancellation, pause/resume, and terminal interrupt controls

The GUI should not assume one active agent per window. Commands must carry explicit backend/session/agent targets.

## Plugin Panels

Semantic UI plugin panels render as GUI controls. The GUI can provide richer rendering for:

- tables
- trees
- forms
- markdown
- code blocks
- diffs
- progress
- notifications

Frontend-specific GUI plugins are allowed, but must be explicit.

## Virtual Terminal Panes

The GUI renders virtual terminal sessions from `Omicron.Terminal.Emulation`.

Features:

- tabs/splits matching shared pane model
- scrollback display
- keyboard/mouse input routing
- copy/paste
- command suggestion insertion
- policy/sandbox badges
- disconnected process state
- remote terminal channels routed through RFC 0014 transports when applicable

The GUI should not bypass the terminal emulator by connecting child process output directly to GUI controls.

## Permissions and Sandbox UX

The GUI should provide clear visual flows for:

- tool approval/rejection
- command execution risk explanation
- sandbox provider/policy selection
- network/file/environment permission requests
- workspace diff review before commit
- rollback/restore from snapshots

## Design Decisions

1. GUI and TUI consume the same core events.
2. GUI does not reuse the terminal renderer.
3. GUI transcript is virtualized.
4. GUI terminal panes render emulator state, not raw child-process output.
5. GUI enriches semantic UI but does not change core semantics.
6. GUI multi-agent controls use RFC 0014 target addressing and attention/subscription semantics.
