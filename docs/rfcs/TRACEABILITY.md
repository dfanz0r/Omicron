# RFC Traceability Matrix

This file maps the original seed document sections in `docs/archive/agent_tui_gui_framework_design.md` to the canonical RFC set.

| Original Section | Canonical RFC Location |
| --- | --- |
| 1. Purpose | `README.md`, RFC 0001 |
| 2. Core Architectural Goals | RFC 0001, RFC 0002, RFC 0003, RFC 0004, RFC 0008 |
| 2.1 Shared agent core | RFC 0001 |
| 2.2 Frontend-specific rendering | RFC 0003, RFC 0004, RFC 0008 |
| 2.3 Plugin-driven extension | RFC 0002, RFC 0010 |
| 2.4 Efficient text handling | RFC 0003 |
| 3. High-Level Package Structure | RFC 0001 |
| 4. Agent Core Model | RFC 0001 |
| 4.1 Omicron agent events | RFC 0001, with persistence/workspace/shell/sandbox events included |
| 4.2 Why events? | RFC 0001 |
| 5. Plugin System | RFC 0002, RFC 0010 |
| 5.1 Headless plugins | RFC 0002 |
| 5.2 Semantic UI plugins | RFC 0002 |
| 5.3 Frontend-specific plugins | RFC 0002, RFC 0008 |
| 6. UI Abstractions | RFC 0002 |
| 6.1 UiNode model | RFC 0002 |
| 6.2 Content blocks | RFC 0002 |
| 6.3 Keep UI abstractions small | RFC 0002 |
| 7. Text Representation | RFC 0003 |
| 7.1 Why not use one giant C# string? | RFC 0003 |
| 7.2 Recommended storage model | RFC 0003 |
| 7.3 Layout index | RFC 0003 |
| 7.4 Grapheme and terminal-cell model | RFC 0003 |
| 8. Terminal Rendering Model | RFC 0003 |
| 8.1 Terminal backend | RFC 0003 |
| 8.2 Frame buffer | RFC 0003 |
| 8.3 Differential renderer | RFC 0003 |
| 8.4 Avoid hot-path console APIs | RFC 0003 |
| 8.5 UTF-8 literals for static terminal output | RFC 0003 |
| 9. Scrollback Design | RFC 0004 |
| 9.1 Native terminal scrollback | RFC 0004 |
| 9.2 App-owned scrollback | RFC 0004 |
| 9.3 Recommended modes | RFC 0004 |
| 10. Transcript Viewport | RFC 0004 |
| 10.1 Recommended pipeline | RFC 0004 |
| 10.2 Block model | RFC 0004 |
| 10.3 Layout cache | RFC 0004 |
| 10.4 Streaming updates | RFC 0004 |
| 11. Layout System | RFC 0004 |
| 12. Input System | RFC 0004, RFC 0002 command system |
| 13. Embedded Virtual Terminal and Shell Panels | RFC 0005 |
| 13.1 Terminal emulation model | RFC 0005 |
| 13.2 Pane orchestration | RFC 0005 |
| 13.3 AI-assisted command writing | RFC 0005 |
| 13.4 Persistence and limitations | RFC 0005, RFC 0006 |
| 14. GUI Frontend | RFC 0008 |
| 15. Relationship to Existing .NET TUI Libraries | RFC 0003 |
| 15.1 Terminal.Gui | RFC 0003 |
| 15.2 Spectre.Console | RFC 0003 |
| 15.3 XenoAtom.Terminal.UI | RFC 0003 |
| 15.4 Custom renderer | RFC 0003 |
| 16. Suggested Internal Fork Strategy | RFC 0003, RFC 0009 benchmarks |
| 17. Rendering Performance Principles | RFC 0003, RFC 0009 benchmarks |
| 18. Permissions and Interactive Requests | RFC 0002 |
| 19. Command System | RFC 0002 |
| 20. Persistence, Session History, and Snapshots | RFC 0006 |
| 20.1 Session history | RFC 0006 |
| 20.2 Session snapshots/checkpoints | RFC 0006 |
| 20.3 Workspace virtual file system | RFC 0006 |
| 20.4 Workspace snapshots | RFC 0006 |
| 20.5 Overlay and transaction model | RFC 0006 |
| 21. Sandboxing Strategy | RFC 0007 |
| 21.1 Candidate sandbox implementations | RFC 0007 |
| 21.2 Sandbox provider abstraction | RFC 0007 |
| 21.3 Policy model | RFC 0007 |
| 21.4 Relationship to VFS, snapshots, and terminal panes | RFC 0007 |
| 21.5 Adoption strategy | RFC 0007, RFC 0009 roadmap |
| 22. Error Handling and Recovery | RFC 0009 |
| 23. Testing Strategy | RFC 0009 |
| 24. Recommended Initial Implementation Plan | RFC 0009, updated with RFC 0010 and RFC 0011 phases |
| 25. Example End-to-End Flow | RFC 0009 |
| 26. Key Design Decisions | `README.md` plus each RFC's Design Decisions section |
| 27. Summary | `README.md` and RFC 0009 Completion Criteria |

## Intentional Additions Since the Seed Document

The RFC set also adds details that were requested after the original seed document:

- IMPLEMENTATION-BASELINE.md: current codebase inventory for the .NET 10 console MVP (`Omicron.Core`, `Omicron.CLI`, `Omicron.Core.Tests`), including implemented provider/API-shape/tool/config features and gaps against the target RFC architecture.

- RFC 0010: WebAssembly plugin runtime with Wasmtime, AssemblyScript host bindings, C# WASM AOT as a future option, and browser-side possibilities.
- RFC 0011: Rust/C# interop and FFI strategy, including C ABI, csbindgen, UniFFI, dotbridge-rs, csharp_binder, and rustc_codegen_clr evaluation.
- RFC 0012: sub-agent orchestration, multiple parallel active agents, VFS locking, hashline/stateful-anchor edit tools, Myers reconciliation, AST/context curation, and harness-design principles from Dirac and Can Bölük's Harness Problem post.
- RFC 0013: content parsing, markdown rendering, and syntax highlighting architecture — incremental markdown parser, Tree-sitter integration, syntax theme system, integration with text store (RFC 0003), content blocks (RFC 0002), and edit harness (RFC 0012).
- RFC 0014: remoting protocol and remote backend architecture — frontend/backend split, binary MessagePack-oriented protocol, explicit backend/session/agent/task target addressing, multiplexed channels, priority-aware flow control, per-agent attention/subscription modes, SSH tunnel bootstrap, detached remote sessions, delegated remote sub-agents, scoped delegated model-provider credential grants, and future QUIC/rendezvous/relay transports.
- RFC 0015: OpenTUI C ABI rendering backend research track — evaluates OpenTUI's Zig core as an alternate native TUI backend through C# bindings, preserving RFC 0003's custom renderer as the fallback/baseline.
- RFC 0002 updates: plugin execution tiers and WASM host-binding alignment.
- RFC 0003 updates: cross-reference to RFC 0013 for content rendering pipeline.
- RFC 0009 updates: content parsing added to Phase Sets 0c, 1a, 2b; new tests, benchmarks, and completion criteria for markdown/syntax.
- REFERENCE-JOURNAL.md: comprehensive annotated index of all READ_ONLY/ reference implementations, mapped to RFCs and roadmap phases.
- README.md update: reference source material section now links to REFERENCE-JOURNAL.md and lists all available reference projects; RFC 0013 added to index.
- RFC 0009 updates: roadmap phases and tests for WASM plugins and Rust/C# FFI.

## Reference Source Material

The `READ_ONLY/` directory contains external/reference code snapshots that may inform Omicron refactors and subsystem implementations. For example, `READ_ONLY/pi-mono/` has reference architecture and plugin API code that can guide Omicron's plugin design. These files are reference-only and should not be edited in place.

When implementation work pulls from `READ_ONLY/`, record the relevant source, license consideration, and Omicron-native destination in the affected RFC or implementation notes.

## Supersession Rule

When this traceability matrix conflicts with an RFC, the RFC is authoritative. This file is only a mapping aid.
