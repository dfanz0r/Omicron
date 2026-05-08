# Reference Implementation Journal

## Purpose

Index and summarize all reference implementations found in `READ_ONLY/` that could inform Omicron's implementation. Each entry notes the project, what it offers, key files/patterns to study, and when in Omicron's roadmap it becomes most relevant.

---

## 1. open-docs — Curated Agent Architecture Documentation

**Path:** `READ_ONLY/open-docs/`

A comprehensive collection of analyzed documentation from major agentic tools.

### Contents

| Directory | Docs | Relevance to Omicron |
|-----------|------|---------------------|
| `docs/claude-agent-sdk/` | Architecture, agents/sub-agents, tools system, MCP, skills, hooks/permissions, design patterns, CLI, prompts, implementation analysis | **High** — sub-agent model, tool system, MCP integration patterns, permission hooks |
| `docs/codex_cli/` | 23-part breakdown of Codex CLI (architecture, prompts, tools, sandboxing, state management, exec mode, UI layer, flow diagrams) | **Very High** — sandboxing architecture, exec-server model, tool system design, state management, flow diagrams |
| `docs/coding-agent/` | Pi coding-agent documentation (project overview, technical stack, API reference, data models, design decisions) | **High** — existing Omicron-like system, confirms data model and package approach |
| `docs/gemini-cli/` | Gemini CLI analysis (stack, API, data models, deployment, design decisions) | **Medium** — multi-turn session handling, command system, model orchestration |
| `docs/opencode/` | 24-part OpenCode breakdown (architecture, tools, CLI, session management, LSP, providers, ACP protocol, MCP, TUI, desktop, security, file system) | **Very High** — ACP protocol, tool implementations, security model, TUI architecture, desktop integration, MCP server patterns |

### Key Documents to Read Before Implementation

- **`docs/codex_cli/07-security-sandboxing.md`** — Sandbox design for RFC 0007
- **`docs/codex_cli/06-tool-system.md`** + **`docs/codex_cli/11-tool-implementations.md`** — Tool model for RFC 0002
- **`docs/codex_cli/09-state-management.md`** + **`docs/codex_cli/20-state-management-practical.md`** — Session state for RFC 0001/0006
- **`docs/opencode/01-architecture.md`** — Overall architecture for Omicron core design
- **`docs/opencode/11-acp-protocol.md`** — ACP protocol (Agent Communication Protocol) for sub-agent ideas
- **`docs/opencode/17-tui-implementation.md`** — TUI patterns for Omicron frontend
- **`docs/claude-agent-sdk/agents-subagents-complete.md`** — Sub-agent patterns for RFC 0012
- **`docs/claude-agent-sdk/tools-system-complete.md`** — Tool system patterns for RFC 0002
- **`docs/codex_cli/22-exec-mode-internals.md`** — Execution internals for RFC 0007

---

## 2. codex — OpenAI Codex CLI (Rust + Python)

**Path:** `READ_ONLY/codex/`

Full Codex CLI source code, primarily Rust with Python skills.

### Key Subsystems

#### Sandboxing (`codex-rs/sandboxing/`, `codex-rs/windows-sandbox-rs/`, `codex-rs/linux-sandbox/`)

| File | What to Study |
|------|---------------|
| `sandboxing/src/lib.rs` | Platform sandbox dispatch: Landlock (Linux), Seatbelt (macOS), bwrap, combined SandboxManager |
| `sandboxing/src/manager.rs` | `SandboxExecRequest`, `SandboxTransformRequest`, `SandboxType` enum, policy transforms |
| `sandboxing/src/policy_transforms.rs` | How permission profiles get translated into sandbox policies |
| `windows-sandbox-rs/src/policy.rs` | `SandboxPolicy` parsing: `read-only`, `workspace-write`, custom JSON policy strings |
| `windows-sandbox-rs/src/lib.rs` | Comprehensive Windows sandbox entrance point |
| `windows-sandbox-rs/src/acl.rs` | ACL-based file access control on Windows |
| `windows-sandbox-rs/src/firewall.rs` | Network restriction on Windows |
| `windows-sandbox-rs/src/conpty/mod.rs` | Windows pseudo-console (ConPTY) integration |
| `windows-sandbox-rs/src/unified_exec/` | Unified execution backends (elevated + legacy), session management |
| `linux-sandbox/src/bwrap.rs` | Bubblewrap sandboxing on Linux |
| `linux-sandbox/src/landlock.rs` | Linux Landlock LSM sandboxing |
| `core/src/sandboxing/mod.rs` | `ExecRequest` struct tying together command, sandbox type, network proxy, permission profile, filesystem/network sandbox policies |

#### Apply Patch (`codex-rs/apply-patch/`)

| File | What to Study |
|------|---------------|
| `apply-patch/src/parser.rs` | V4A diff format parser — important for understanding edit format compatibility |
| `apply-patch/src/lib.rs` | Core apply-patch logic |
| `apply-patch/src/invocation.rs` | How the tool is invoked and integrated |
| `apply-patch/src/seek_sequence.rs` | Fuzzy matching for patch application |

#### Exec Server (`codex-rs/exec-server/`)

| File | What to Study |
|------|---------------|
| `exec-server/src/lib.rs` | Execution server entry point |
| `exec-server/src/fs_sandbox.rs` | Filesystem sandboxing in exec server |
| `exec-server/src/local_process.rs` | Local process execution |
| `exec-server/src/process.rs` | Process abstraction |
| `exec-server/src/protocol.rs` | Exec server protocol |

#### Core (`codex-rs/core/src/`)

| File | What to Study |
|------|---------------|
| `tools/sandboxing.rs` | Tool-level sandbox integration |
| `sandbox_tags.rs` | Sandbox tagging/conventions |

### Relevance to Omicron

- **RFC 0007 (Sandboxing):** Primary reference. Policy model, platform-specific backends, unified execution interface, exec-server pattern.
- **RFC 0002 (Tools):** Apply-patch tool as a reference for edit tool design.
- **RFC 0011 (FFI):** Codex is entirely Rust; Omicron would adapt these patterns to C#/Rust interop.

### When to Study Deeply

- Phase Set 3a (Sandboxing): Study the full `sandboxing/`, `windows-sandbox-rs/`, and `linux-sandbox/` crates.
- Phase Set 3b (Edit Harness): Study `apply-patch/` parser and fuzzy matching.
- Phase Set 4a (Virtual Terminal): Study `windows-sandbox-rs/src/conpty/` for PTY patterns on Windows.

---

## 3. zed — Zed Code Editor (Rust)

**Path:** `READ_ONLY/zed/`

The full Zed editor codebase. Extremely mature Rust architecture with agent, terminal, and editing subsystems.

### Key Cargo Crates

| Crate | Path | What to Study |
|-------|------|---------------|
| `agent` | `crates/agent/` | Agent loop, edit agent, tools, spawn_agent_tool, thread management, permissions |
| `terminal` | `crates/terminal/` | Terminal emulator (wraps alacritty_terminal), PTY, settings |
| `terminal_view` | `crates/terminal_view/` | Terminal rendering in GPUI |
| `language` | `crates/language/` | Buffer, anchor, point, text buffer abstractions |
| `project` | `crates/project/` | Project model, worktree, file system, LSP |
| `language_model` | `crates/language_model/` | LLM integration, providers, streaming |
| `editor` | `crates/editor/` | Text editor, buffers, multi-buffer, selections |
| `rope` | `crates/rope/` | Text rope data structure (append-only, indexable) |
| `text` | `crates/text/` | Text buffer primitives |
| `streaming_diff` | `crates/streaming_diff/` | Streaming diff algorithm — relevant for edit harness |
| `acp_thread` | `crates/acp_thread/` | ACP agent thread, sub-agent session info |
| `acp_tools` | `crates/acp_tools/` | ACP tools implementation |
| `workspace` | `crates/workspace/` | Workspace model, tabs, pane management |

### Agent-Specific Files to Study

| File | What to Study |
|------|---------------|
| `agent/src/agent.rs` | Full agent loop, tool dispatch, message processing |
| `agent/src/edit_agent.rs` | Edit agent: streaming diff, fuzzy matching, edit formats, reindent |
| `agent/src/edit_agent/streaming_fuzzy_matcher.rs` | Fuzzy matcher for streaming model output to file content |
| `agent/src/tools.rs` | Tool registry: `macro_rules! tools` pattern, `ALL_TOOL_NAMES` |
| `agent/src/tools/spawn_agent_tool.rs` | **Very relevant for RFC 0012 sub-agents**: sub-agent spawning, session_id, parallel delegation, structured output |
| `agent/src/tools/terminal_tool.rs` | Terminal execution tool with timeout, cwd, command output limiting |
| `agent/src/tools/edit_file_tool.rs` | **Relevant for RFC 0012 edit harness**: edit file implementation |
| `agent/src/tools/streaming_edit_file_tool.rs` | Streaming edit tool |
| `agent/src/tools/read_file_tool.rs` | File reading with anchor/line annotations |
| `agent/src/tools/tool_permissions.rs` | Permission model per tool |
| `agent/src/thread.rs` | Thread/session lifecycle |
| `agent/src/native_agent_server.rs` | ACP server implementation |

### Terminal-Specific Files to Study

| File | What to Study |
|------|---------------|
| `terminal/src/terminal.rs` | Full terminal emulator wrapping alacritty_terminal, PTY management, resize, selection, events, hyperlinks |
| `terminal/src/terminal_hyperlinks.rs` | Terminal hyperlink support (OSC 8) |
| `terminal/src/pty_info.rs` | PTY process info, PID tracking |
| `terminal/src/mappings/` | Key mappings, mouse mappings, color conversion |
| `terminal_view/src/terminal_view.rs` | Terminal view rendering, cursor, selection interaction |

### Relevance to Omicron

- **RFC 0013 (Content Parsing):** The markdown parser (`crates/markdown/`) is the primary reference for incremental markdown parsing with byte-range annotations. The language/grammar system (`crates/language/`, `crates/language_core/`, `crates/grammars/`) is the primary reference for Tree-sitter-based syntax highlighting.
- **RFC 0012 (Agent Orchestration):** `spawn_agent_tool.rs` is the best reference for sub-agent tool design.
- **RFC 0012 (Edit Harness):** `edit_agent.rs`, `streaming_fuzzy_matcher.rs`, `streaming_diff` crate — all directly relevant.
- **RFC 0005 (Virtual Terminal):** `terminal/` crate is an excellent reference for VT emulator wrapping, PTY management.
- **RFC 0003 (Text):** `rope/` and `text/` crates for append-only indexed text storage.
- **RFC 0002 (Tools):** Tool registry pattern, permission model.
- **RFC 0007 (Sandboxing):** Not directly sandboxed (Zed is a local editor) but the project/worktree isolation patterns are useful.

### Language/Syntax-Specific Files to Study (RFC 0013)

| File | What to Study |
|------|---------------|
| `language/src/language.rs` | `Language` type, Tree-sitter integration, syntax map, buffer/tree bridge, `with_parser` pool pattern |
| `language/src/language_registry.rs` | `LanguageRegistry`: grammar loading, config, LSP adapter management |
| `language/src/syntax_map.rs` | `SyntaxMap`/`SyntaxSnapshot` — maps buffer byte ranges to Tree-sitter syntax captures. Uses SumTree for O(log n) range queries. Handles incremental updates, injection languages, foldable regions. 2000+ lines of critical architecture. |
| `language_core/src/grammar.rs` | `Grammar` struct: Tree-sitter `Language`, `HighlightsConfig` (query + capture indices), bracket configs, indent/outline configs, highlight map |
| `language_core/src/highlight_map.rs` | `HighlightMap`: capture IDs → `HighlightId` mapping for theme lookup |
| `language_core/src/language_config.rs` | `LanguageConfig`: file extensions, scope names, bracket pairs, indent settings, comment syntax |
| `grammars/src/grammars.rs` | Built-in grammar registration: 20+ native Tree-sitter grammars via `RustEmbed`, per-language `config.toml` |
| `grammars/src/*/config.toml` | Per-language Tree-sitter configs, queries, scope mapping |
| `syntax_theme/src/syntax_theme.rs` | Syntax theme model: TextMate scope→color mapping |
| `markdown/src/parser.rs` | **Primary RFC 0013 reference**: pulldown-cmark incremental parser, `ParsedMarkdownData` with byte-range events, language names, heading slugs, GFM options, HTML blocks. 1300+ lines. |

### When to Study Deeply

- Phase Set 0c (Text): Study `rope/` data structure
- Phase Set 2b (Rich Content): Study `markdown/src/parser.rs`, `language/src/syntax_map.rs`, `language_core/src/grammar.rs`, `syntax_theme/`
- Phase Set 3b (Edit Harness): Study `edit_agent.rs`, `streaming_fuzzy_matcher.rs`, `streaming_diff/`
- Phase Set 4a (Virtual Terminal): Study `terminal/` and `terminal_view/`
- Phase Set 4b (Sub-Agents): Study `spawn_agent_tool.rs`

---

## 4. dirac — Dirac Agent CLI (TypeScript)

**Path:** `READ_ONLY/dirac/`

Focused on token-efficient editing with hash anchors, Myers diff reconciler, and AST context curation.

### Key Files

| File | What to Study |
|------|---------------|
| `cli/src/utils/DiffComputer.ts` | **Very relevant**: Search/replace block parsing, Myers diff computation, line-level diffs, SEARCH/REPLACE format parser |
| `cli/src/utils/task-history.ts` | Task history tracking by workspace |
| `cli/src/utils/parser.ts` | JSON parsing utilities |
| `cli/src/utils/mode-selection.ts` | Mode selection logic |
| `cli/src/utils/provider-config.ts` | Provider configuration |
| `cli/src/utils/slash-commands.ts` | Slash command parsing |
| `cli/src/agent/DiracAgent.ts` | Agent loop, permission handling |
| `cli/src/agent/DiracSessionEmitter.ts` | Session event emitting |
| `cli/src/agent/messageTranslator.ts` | Model message translation |
| `cli/src/agent/permissionHandler.ts` | Permission request handling |
| `cli/src/acp/AcpAgent.ts` | ACP-compatible agent implementation |
| `cli/src/acp/AcpTerminalManager.ts` | Terminal management |
| `cli/src/hooks/useTextInput.ts` | Text input hook |
| `cli/src/hooks/useTerminalSize.ts` | Terminal resize handling |
| `cli/src/constants/keyboard.ts` | Keybinding definitions |
| `cli/src/constants/colors.ts` | Color scheme |

### Relevance to Omicron

- **RFC 0012 (Edit Harness):** `DiffComputer.ts` is the key reference for SEARCH/REPLACE parsing, Myers diff, and line-level diff computation. Dirac's whole thesis is single-token anchors + Myers diff reconciler.
- **RFC 0001 (Events):** Session emitter pattern.
- **RFC 0002 (Plugins):** Slash command system.
- **RFC 0004 (Input):** useTextInput pattern.

### When to Study Deeply

- Phase Set 3b (Edit Harness): Study the entire `cli/src/utils/` directory, especially `DiffComputer.ts`, and the agent edit flow in `DiracAgent.ts`.

---

## 5. pi-mono — Pi Coding Agent (TypeScript)

**Path:** `READ_ONLY/pi-mono/`

The original Pi coding agent monorepo. Omicron's spiritual predecessor in the .NET ecosystem.

### Key Files

| File | What to Study |
|------|---------------|
| `packages/coding-agent/src/core/extensions/types.ts` | **Very relevant**: Full extension API type definitions — tools, commands, events, UI providers, lifecycle hooks |
| `packages/coding-agent/src/core/extensions/loader.ts` | Extension module loader using jiti, virtual modules, bundling pattern |
| `packages/coding-agent/src/core/extensions/runner.ts` | Extension execution lifecycle |
| `packages/coding-agent/src/core/extensions/wrapper.ts` | API wrapper/proxy for extensions |
| `packages/coding-agent/src/core/agent-session.ts` | Session model |
| `packages/coding-agent/src/index.ts` | Public API exports |
| `packages/ai/` | AI provider abstractions |
| `packages/agent/` | Core agent package |

### Documentation Files

| Doc | Relevance |
|-----|-----------|
| `docs/extensions.md` | Extension authoring guide |
| `docs/tui.md` | TUI system documentation |
| `docs/skills.md` | Skills/agent customization |
| `docs/packages.md` | Package structure |
| `docs/session.md` | Session management |
| `docs/sdk.md` | SDK documentation |
| `docs/providers.md` | AI provider system |

### Relevance to Omicron

- **RFC 0002 (UI/Plugins):** Extension system types and loader are directly applicable to Omicron's plugin architecture. The TypeScript patterns can be adapted to C# interfaces.
- **RFC 0001 (Core):** Agent session model.
- **RFC 0006 (Persistence):** Session persistence patterns.

### When to Study Deeply

- Phase Set 1a (Semantic UI/Plugins): Study `extensions/types.ts` for the API surface design.
- Phase Set 1b (Persistence): Study `agent-session.ts` for session patterns.
- Phase Set 2b (Rich Content): Study how Pi renders markdown and code blocks in the TUI for reference.

---

## 6. ConsoleEx AgentStudio + Markup System — .NET TUI Agent UI & Markup Rendering

**Path:** `READ_ONLY/ConsoleEx/Examples/AgentStudio/`, `READ_ONLY/ConsoleEx/SharpConsoleUI/Parsing/`

A reference .NET TUI coding-agent UI and a Spectre-compatible markup parser for styled terminal text.

### Key Files — AgentStudio

| File | What to Study |
|------|---------------|
| `AgentStudioWindow.cs` | **Very relevant**: Full TUI agent UI layout — status bars, conversation panel, input area, mode selection, markers, cursor line |
| `Components/ToolCallPanel.cs` | Tool call visual rendering with status icons, expandable output |
| `Components/AnalysisPanel.cs` | Analysis/inspection panel |
| `Modals/CommandPaletteModal.cs` | Command palette implementation |
| `Modals/SessionManagerModal.cs` | Session management UI |
| `Models/Message.cs` | Message model |
| `Models/ToolCall.cs` | Tool call data model with status, timing |
| `Services/MockAiService.cs` | Mock AI service for testing |

### Key Files — Markup System

| File | What to Study |
|------|---------------|
| `SharpConsoleUI/Parsing/MarkupParser.cs` | **Very relevant for RFC 0013**: Spectre-compatible markup parser converting `[color]text[/]` tags to cell sequences. Handles gradient tags, nested styles, RGB/hex/named colors, Unicode width. 941 lines. |
| `SharpConsoleUI/Parsing/MarkupStyle.cs` | Style model: foreground, background, text decoration |
| `docs/controls/MarkupControl.md` | Markup syntax documentation for Spectre-compatible rich text |

### Relevance to Omicron

- **RFC 0004 (TUI Layout):** AgentStudio layout matches Omicron's described layout pattern.
- **RFC 0004 (Rich Content):** ToolCallPanel rendering is directly applicable.
- **RFC 0013 (Content Parsing):** The `MarkupParser` shows how to parse inline markup styles into cells — directly relevant to Omicron's markup/rich-text rendering pipeline.

### When to Study Deeply

- Phase Set 2 (TUI Integration): Study AgentStudio layout and ToolCallPanel.
- Phase Set 2b (Rich Content): Study `MarkupParser.cs` for TUI inline style rendering.

---

## 7. XenoAtom.Terminal — .NET Terminal Backend

**Path:** `READ_ONLY/XenoAtom.Terminal/`

A .NET terminal abstraction library that Omicron's RFC 0003 recommends as a reference/fork candidate.

### Key Files

| File | What to Study |
|------|---------------|
| `src/XenoAtom.Terminal/Backends/ITerminalBackend.cs` | Terminal backend interface: size, cursor, events, capabilities, output/error writers |
| `src/XenoAtom.Terminal/Backends/UnixTerminalBackend.cs` | Unix terminal backend with terminfo |
| `src/XenoAtom.Terminal/Backends/WindowsConsoleTerminalBackend.cs` | Windows console backend |
| `src/XenoAtom.Terminal/Backends/VirtualTerminalBackend.cs` | Virtual/in-memory terminal backend for testing |
| `src/XenoAtom.Terminal/Backends/InMemoryTerminalBackend.cs` | In-memory backend |
| `src/XenoAtom.Terminal/Internal/TerminalKeyMappings.cs` | Key input mapping |
| `src/XenoAtom.Terminal/Internal/TerminalTextReader.cs` | Text input reader |
| `src/XenoAtom.Terminal/Internal/AnsiBuilderTextWriter.cs` | ANSI output building |
| `src/XenoAtom.Terminal/Internal/Osc52Clipboard.cs` | OSC 52 clipboard support |

### Relevance to Omicron

- **RFC 0003 (Terminal Backend):** The `ITerminalBackend` interface is a direct reference for Omicron's terminal backend abstraction.
- **RFC 0004 (Input):** Key mapping and text reader patterns.

### When to Study Deeply

- Phase Set 0c (Text/Terminal): Study `ITerminalBackend.cs` and platform implementations.

---

## 8. XenoAtom.Terminal.UI — .NET TUI Framework

**Path:** `READ_ONLY/XenoAtom.Terminal.UI/`

A .NET TUI framework built on XenoAtom.Terminal, with controls and layout.

### Key Files

| File | What to Study |
|------|---------------|
| `samples/ControlsDemo/Demos/` | 50+ control demos showing TUI widgets (accordion, button, checkbox, code editor, command palette, data grid, list, markdown, menu, progress, splitter, table, tabs, tree, text input) |
| `samples/ControlsDemo/ControlsDemoApp.cs` | Application shell pattern |
| `samples/ControlsDemo/DemoRegistry.cs` | Registry pattern for widgets |

### Relevance to Omicron

- **RFC 0004 (TUI Widgets):** The control demos show what a mature .NET TUI framework offers. Omicron should reference these widget designs but build custom high-performance versions for streaming transcript data.

### When to Study Deeply

- Phase Set 2 (TUI Integration): Browse control demos for ideas on splitter, tabs, scrollable panels, tree views, and rich text rendering in TUI.

---

## 9. termina — .NET TUI Application Framework

**Path:** `READ_ONLY/termina/`

A .NET TUI framework with explicit focus on high-performance rendering, double-buffering, and streaming content.

### Key Files

| File | What to Study |
|------|---------------|
| `src/Termina/Terminal/FrameBuffer.cs` | **Very relevant**: Double-buffered terminal cell grid, diff-based rendering, Clear/Resize/GetCell |
| `src/Termina/Terminal/DiffingTerminal.cs` | **Very relevant**: Terminal diffing for cell-level changes |
| `src/Termina/Terminal/TerminalCell.cs` | Cell model with char, foreground, background, attributes |
| `src/Termina/Rendering/IRenderable.cs` | Renderable component interface |
| `src/Termina/Rendering/IRenderContext.cs` | Render context abstraction |
| `src/Termina/Rendering/Panel.cs` | Panel layout with border, scrollable content |
| `src/Termina/Rendering/Text.cs` | Text rendering |
| `src/Termina/Rendering/TextInput.cs` | Text input rendering |
| `src/Termina/Components/Streaming/PersistedStreamBuffer.cs` | **Very relevant**: Append-only streaming text buffer with persistence |
| `src/Termina/Components/Streaming/WindowedStreamBuffer.cs` | **Very relevant**: Windowed view over streaming buffer |
| `src/Termina/Components/Streaming/IStreamingTextBuffer.cs` | Streaming buffer interface |
| `src/Termina/Layout/ScreenBounds.cs` | Screen bounds/layout |
| `benchmarks/Termina.Benchmarks/RenderingBenchmarks.cs` | Rendering performance benchmarks |

### Relevance to Omicron

- **RFC 0003 (Text/Rendering):** `FrameBuffer.cs`, `DiffingTerminal.cs`, and `TerminalCell.cs` are directly relevant. The double-buffering and diff pattern is exactly what Omicron's RFC 0003 describes.
- **RFC 0003 (Transcript Storage):** `PersistedStreamBuffer` and `WindowedStreamBuffer` are very close to Omicron's `Utf8TextStore` concept.
- **RFC 0009 (Benchmarks):** Termina's rendering benchmarks are a template for Omicron's own benchmarks.

### When to Study Deeply

- Phase Set 0c (Text/Terminal): Study `FrameBuffer.cs`, `DiffingTerminal.cs`, `TerminalCell.cs`, streaming buffers.
- Phase Set 2 (TUI Integration): Study rendering pipeline layout.

---

## 10. opencode — OpenCode Agent (TypeScript/Bun)

**Path:** `READ_ONLY/opencode/`

OpenCode is a large agentic coding platform with CLI, VSCode extension, desktop app, and cloud console.

### Package Structure

| Package | What to Study |
|---------|---------------|
| `packages/core` | Core utilities (filesystem, cross-spawn, hashing, flags) |
| `packages/opencode` | Main agent logic: account, acp, agent, auth, bus, cli, command, config, control-plane, env, file, format, git, id, ide, lsp, mcp, patch, permission, plugin, project, provider, pty, question, server, session, share, shell, skill, snapshot, storage, sync, tool, util, v2, worktree |
| `packages/app` | Web app UI |
| `packages/console` | Cloud console |
| `packages/desktop` | Tauri desktop app |
| `packages/ui` | Shared UI components |

### Relevance to Omicron

- **RFC 0015 (OpenTUI Backend):** OpenCode's TUI uses `@opentui/core` and `@opentui/solid`, with SolidJS fine-grained reactivity feeding OpenTUI's native Zig renderer. Study `packages/opencode/src/cli/cmd/tui/` for production OpenTUI usage patterns.
- **RFC 0013 (Content Parsing):** `packages/opencode/parsers-config.ts` is a reference for Tree-sitter WASM grammar loading — configuration format for 12+ languages with highlight/locals queries from web URLs.
- **Multiple RFCs**: The structured packages map well to Omicron's planned package boundaries. The `opencode/src/` directory has a `tool/`, `agent/`, `session/`, `mcp/`, `permission/`, `provider/`, `pty/`, `shell/`, `snapshot/`, `worktree/` organization that aligns with Omicron's package divisions.
- **RFC 0007 (Sandboxing):** `permission/` and security model.
- **RFC 0005 (Virtual Terminal):** `pty/` and `shell/` packages.
- **RFC 0006 (Persistence):** `session/`, `snapshot/`, `storage/`.
- **RFC 0012 (Edit Harness):** `patch/` package.

### When to Study Deeply

- Phase Set 0c / RFC 0015: Study `packages/opencode/src/cli/cmd/tui/` and OpenTUI usage before deciding the TUI backend.
- Phase Set 2b (Rich Content): Study `parsers-config.ts` for Tree-sitter WASM grammar loading pattern.
- Phase Sets 3-5: Study the `opencode/src/organization` for reference on how to structure a large agentic application.

---

## 11. OpenTUI — Native Zig Terminal UI Core

**Path:** `READ_ONLY/opentui/`

OpenTUI is a native terminal UI core written in Zig with TypeScript bindings. Its documentation states that the Zig core exposes a C ABI usable from any language. It powers OpenCode's production TUI.

### Key Areas

| Path | What to Study |
|------|---------------|
| `packages/core/src/zig/lib.zig` | C ABI export surface: renderer, buffers, text buffers, editor views, callbacks |
| `packages/core/src/zig/renderer.zig` | Render lifecycle and diffed terminal output |
| `packages/core/src/zig/buffer.zig` | Optimized terminal buffers and cell representation |
| `packages/core/src/zig/text-buffer*.zig` | Text storage, wrapping, viewport support |
| `packages/core/src/zig/edit-buffer.zig` / `editor-view.zig` | Prompt/editor primitives |
| `packages/core/src/` TypeScript bindings | How Bun FFI maps the C ABI into higher-level API |

### Relevance to Omicron

- **RFC 0015 (OpenTUI Backend):** Primary reference for C# binding spike.
- **RFC 0003 (Rendering):** Candidate replacement for custom terminal backend/frame diff layer.
- **RFC 0004 (Transcript Viewport):** TextBuffer/View may inform or accelerate viewport implementation.
- **RFC 0005 (Virtual Terminal):** Buffer copy/draw APIs may support terminal pane rendering.
- **RFC 0011 (FFI):** Real native C ABI binding target for Omicron.

### When to Study Deeply

- Phase Set 0c: Build C# P/Invoke hello-world and buffer throughput spike.
- Phase Set 1c/2a: Decide whether transcript viewport uses Omicron custom layout or OpenTUI TextBuffer/View.

---

## 12. oh-my-pi — Forked Pi Agent (TypeScript + Rust)

**Path:** `READ_ONLY/oh-my-pi/`

A fork of Pi with substantial enhancements including Rust-native subsystems via N-API.

### Key Components

| Component | What to Study |
|-----------|---------------|
| `crates/brush-builtins-vendored/` | Rust-native shell builtins (alias, bg, bind, cd, echo, eval, exec, exit, export, fc, fg, hash, history, jobs, kill, let, local, popd, pushd, pwd, read, readonly, return, set, shift, source, suspend, test, times, trap, type, typeset, ulimit, umask, unalias, unset, wait) — a full shell builtin library in Rust |
| `src/` (top-level) | TypeScript agent code |
| `.omp/` | oh-my-pi's own Omicron plugin config |

### Relevance to Omicron

- **RFC 0011 (FFI):** This is the best reference for a mixed TypeScript+Rust agent application using N-API. The Rust shell builtins crate shows what can be moved to Rust for performance.
- **RFC 0005 (Virtual Terminal):** The brush shell builtins are relevant if Omicron needs to embed shell logic.

### When to Study Deeply

- Phase Set 0b (FFI): Study the N-API interop pattern to understand how a mixed application operates.

---

## 13. open-claude-code — Open Claude Code (JavaScript)

**Path:** `READ_ONLY/open-claude-code/`

Minimal open-source implementation of Claude Code's architecture.

### Key Files

| File | What to Study |
|------|---------------|
| `archive/open_claude_code/src/cli/app.mjs` | CLI application structure |
| `archive/open_claude_code/src/terminal/ui.mjs` | Terminal UI implementation |
| `archive/open_claude_code/src/api/client.mjs` | API client |
| `archive/open_claude_code/src/wasm/` | WASM-based layout engine and terminal renderer |

### Unique Feature: WASM Layout Engine

The `src/wasm/` directory contains a Yoga-based layout engine running in WASM:
- `layout-engine.mjs` — WASM layout calculations
- `terminal-renderer.mjs` — Terminal rendering
- `ui-components.mjs` — UI component system
- `conversation-ui.mjs` — Conversation UI
- `yoga-loader.mjs` — Yoga layout engine loader

### Relevance to Omicron

- **RFC 0004 (Layout):** WASM-accelerated layout engine is an interesting future direction.
- **RFC 0010 (WASM):** Concrete example of WASM for UI rendering.

### When to Study Deeply

- Phase Set 3c (WASM): Study the WASM layout engine pattern for future WASM-accelerated layout ideas.

---

## 14. kilocode — Kilo Code Agent (TypeScript)

**Path:** `READ_ONLY/kilocode/`

Kilo Code (appears to be built on the same platform as opencode, similar structure with `packages/app/`, `packages/opencode/`, `packages/desktop/`).

### Relevance to Omicron

- Similar structure and patterns to opencode. Worth cross-referencing during implementation for alternative approaches.

---

## 15. gemini-cli — Google Gemini CLI

**Path:** `READ_ONLY/gemini-cli/`

Google's Gemini CLI tool, primarily configuration/skills-based rather than source-code-driven.

### Key Files

| File | What to Study |
|------|---------------|
| `.gemini/config.yaml` | Configuration model |
| `.gemini/commands/` | Command definitions in TOML format |
| `.gemini/skills/` | Skill definitions with policies and scripts |
| `evals/` | Evaluation test framework |

### Relevance to Omicron

- **RFC 0002 (Commands):** The TOML command definitions are a reference for extensible command definition.
- **RFC 0009 (Testing):** The eval framework structure is a good reference for Omicron's testing approach.

---

## 16. pi-opencode-provider — Provider Bridge

**Path:** `READ_ONLY/pi-opencode-provider/`

A minimal bridge to use OpenCode as a provider for Pi. Small TypeScript package.

### Relevance to Omicron

- **RFC 0001 (Providers):** Provider bridge pattern for model provider abstraction.

---

## Cross-Cutting Patterns Summary

### Agent Loop Patterns
- **Zed**: Full agent with tool dispatch, streaming edits, sub-agents (Rust)
- **Pi mono**: TypeScript agent loop with extension system
- **Dirac**: TypeScript agent with hash-anchor editing
- **Codex**: Rust agent with sandbox execution

### Edit Tool Patterns
- **Zed streaming_diff + fuzzy matcher**: Streaming model output matched to file content with fuzzy matching
- **Dirac DiffComputer**: SEARCH/REPLACE parsing, Myers diff
- **Codex apply_patch**: V4A diff format parser and applicator
- **Hashline (conceptual)**: Content-hash based line anchors from Harness Problem

### Terminal Architecture Patterns
- **Zed/alacritty_terminal**: Battle-tested VT parser + screen buffer + PTY (Rust)
- **XenoAtom.Terminal**: .NET terminal backend abstraction
- **Termina FrameBuffer**: .NET double-buffered diff rendering
- **Codex windows-sandbox-rs/conpty**: Windows ConPTY integration

### Sandbox Patterns
- **Codex**: Multi-platform (Landlock + bwrap + Seatbelt + Windows ACL), unified SandboxManager, exec server
- **codex-protocol**: Policy definition (read-only, workspace-write, custom JSON)

### Sub-Agent Patterns
- **Zed spawn_agent_tool**: Delegated tasks with session ID, structured output, disjoint write sets, parallel execution
- **Codex**: External agent config, MCP-based sub-agent communication

### Extension/Plugin Patterns
- **Pi mono**: TypeScript extension system with virtual modules, lifecycle hooks, tool/command/panel registration
- **OpenCode**: Plugin system with ACP protocol

### Markdown / Content Parsing Patterns
- **Zed markdown parser**: Incremental pulldown-cmark parser with byte-range `MarkdownEvent` stream. Tracks root block starts, language names, heading slugs, HTML blocks. 1300+ lines of battle-tested implementation.
- **Zed language system**: Tree-sitter grammars via `Grammar` + `LanguageRegistry` + `SyntaxMap` (SumTree-indexed capture ranges). 20+ built-in grammars in Rust, WASM grammars for dynamic loading.
- **OpenCode parsers-config.ts**: Tree-sitter WASM grammar loading configuration — references web-hosted `.wasm` files and `.scm` query files for 12+ languages.
- **ConsoleEx MarkupParser**: Spectre-compatible inline markup parser for terminal-rich text — good reference for TUI syntax style rendering.

### Rendering Patterns
- **Termina**: Frame Buffer double-buffering, cell-level diffing, streaming buffers
- **ConsoleEx AgentStudio**: Full agent TUI with status bars, tool call panels, conversation display
- **XenoAtom.Terminal.UI**: Rich control library in .NET TUI
