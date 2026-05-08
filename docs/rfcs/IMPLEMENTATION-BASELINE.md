# Current Implementation Baseline

Last reviewed: 2026-05-08

This document records the codebase state that the RFCs currently target. It is descriptive, not aspirational: when it conflicts with an individual RFC's future-state design, this baseline describes what exists today and the RFC describes the intended direction.

## Solution and Projects

Current solution layout:

```text
Omicron.slnx
Omicron.Core/          net10.0 shared agent/provider/tool/config code
Omicron.CLI/           net10.0 console MVP frontend
Omicron.Core.Tests/    xUnit tests for core/provider behavior
READ_ONLY/             external/reference snapshots, not edited in place
```

The broader package layout in RFC 0001 is still the target architecture. The current codebase has not yet split out `Omicron.UI.Abstractions`, `Omicron.Text`, `Omicron.Rendering.Terminal`, `Omicron.Terminal.Emulation`, `Omicron.Persistence`, `Omicron.Workspace`, `Omicron.Sandboxing`, `Omicron.Plugins`, `Omicron.Plugins.Wasm`, `Omicron.Remoting`, or dedicated frontend projects.

## Implemented Core MVP

Implemented in `Omicron.Core`:

- Stateful `Agent` loop with a conversation transcript, tool registration, reset, max-iteration guard, and streaming `IAsyncEnumerable<AgentEvent>` output.
- Current agent event enum: `Start`, `TextDelta`, `ToolCallStart`, `ToolCallEnd`, `Response`, `Error`.
- Message model with user, assistant, tool-result, image content, tool-call content, reasoning text, usage, stop reason, and timestamps.
- Simple `Tool` contract with JSON-schema parameters and async string result.
- Provider abstraction (`IChatProvider`) with streaming and non-streaming convenience collection.
- Provider/model separation: `Model` carries provider name, base URL, `ApiType`, context window, token limits, image/reasoning flags, costs, and resolved provider instance.
- Shape-based provider base for providers that differ mainly by wire format.
- TOML-backed config manager storing API keys and general settings.

Current event contracts are simpler than RFC 0001's planned append-only typed record stream. They are adequate for the console MVP but are not yet persistence/replay-grade.

## Implemented Providers and API Shapes

Implemented provider classes:

- `OpenAiProvider`
- `AnthropicProvider`
- `OpenCodeProvider` for OpenCode Zen and Go model discovery/use
- `OpenRouterProvider`
- `ProviderFactory`

Implemented/declared API shapes:

- `OpenAiChat`
- `AnthropicMessages`
- `OpenAiResponses`
- `GoogleGenAi` is declared in `ApiType` but not a complete first-class provider path yet.

Provider support includes SSE parsing, tool-call accumulation, usage data where available, and reasoning-text preservation/echo behavior needed by reasoning models such as DeepSeek-style APIs.

## Implemented Tools

Registered by the CLI:

- `calculator` inline helper.
- `get_current_time` inline helper.
- `read_path` via `FileTools.Create(workspaceRoot)`.
- `shell` via `ShellTools.Create(workspaceRoot)`.

Current file/shell tools are direct host tools. They enforce workspace-root path containment and output truncation, but they do not yet go through the RFC 0006 VFS, RFC 0007 execution broker, permission prompts, audit events, or sandbox providers.

`read_path` currently supports:

- file reads with line numbers;
- offset/limit and chunk-based continuation;
- binary detection;
- directory listings with counts/sizes;
- truncation around 50 KB / 2000 lines.

`shell` currently supports:

- dynamic detection of common shells (`bash`, `sh`, `zsh`, `fish`, `dash`, `pwsh`, `powershell`, `cmd` as available);
- workspace-confined `cwd` resolution;
- timeout clamping;
- output truncation around 50 KB / 2000 lines.

## Implemented CLI MVP

Implemented in `Omicron.CLI`:

- Startup banner and provider registration display.
- Model discovery on startup and manual `refresh`.
- Fallback built-in models for OpenAI and Anthropic.
- Model menu showing keyed/free providers and last-used model first.
- TOML config UI for API keys, system prompt, max tokens, temperature, display width/line limits, and max iterations.
- Basic line editor (`LineEditor`) for chat input.
- Chat loop with `exit`, `reset`, streaming output, Escape-to-cancel polling, tool-call display, and usage display.
- Output truncation helper for long tool results.

This is an inline console frontend, not the planned fullscreen TUI in RFC 0003/0004/0009 and not a separate `Omicron.Frontend.Tui` project.

## Not Yet Implemented

The following RFC capabilities remain planned/research unless otherwise noted in code:

- Persistence/session event logs, snapshots, replay, and resume.
- Workspace VFS, overlays, transactions, diff manifests, and host reconciliation.
- Permission system and command system.
- Plugin model, semantic UI abstractions, panels/status providers, WASM runtime.
- Text store, grapheme/cell layout, frame buffers, differential renderer, app-owned fullscreen scrollback.
- Embedded PTY/virtual terminal panes.
- Sandboxing/execution broker/audit policy model.
- High-reliability edit harness, anchors, AST context curation, multi-file transactions.
- Remoting protocol/backends/attachment model.
- GUI frontend.
- Rust/C# FFI layer and OpenTUI native backend spike.
- Markdown parsing, syntax highlighting, theme system.

## Current Tests

`Omicron.Core.Tests` currently covers core message/model/tool behavior, agent loop behavior with fake providers, provider parsing/shape behavior, and config/provider helper behavior.

As of this review, `dotnet test Omicron.slnx --nologo` passes after restoring compatibility between `AssistantToolCallMessage` and both the legacy single-tool-call property plus the newer multi-tool-call list.

## RFC Targeting Notes

Near-term work should treat the current code as a Phase 0 MVP:

1. Harden `AgentEvent` and `StreamEvent` into the stable append-only contracts in RFC 0001 before building persistence or remoting on top.
2. Move direct host file/shell tools behind permission, VFS, and execution broker seams before expanding tool authority.
3. Keep `Omicron.CLI` as the smoke-test console frontend while extracting core abstractions into future packages.
4. Preserve provider/API-shape separation; it is one of the MVP pieces already aligned with the target architecture.
5. Update this baseline whenever substantial implementation changes land, so roadmap work can be targeted against reality rather than only the long-term RFC design.
