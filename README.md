# Omicron

Omicron is an experimental C# AI coding-agent platform. The current codebase is a compact .NET console MVP, and the active project direction is to evolve it into a modular core that can support richer frontends, extensions, durable sessions, workspace safety, and future sandboxing/remoting capabilities.

## Current State

The repository currently contains:

```text
Omicron.Core/          Core agent loop, providers, models, tools, and config
Omicron.CLI/           Console MVP frontend (TUI + classic CLI modes)
Omicron.Core.Tests/    xUnit tests
READ_ONLY/             External/reference snapshots, not edited in place
docs/rfcs/             Canonical architecture RFCs
docs/implementation-plans/ Tactical implementation plans
docs/reports/          Audit reports and gap analyses
```

The MVP already supports:

- streaming agent responses;
- multiple provider/API-shape paths (OpenAI Responses, Anthropic, OpenRouter, etc.);
- model discovery with metadata refresh;
- TOML-backed configuration with API key management;
- tool calling (file read, shell execution, workspace operations);
- direct file and shell tools with basic workspace containment;
- a simple console chat loop;
- **a rich terminal UI (TUI)** with:
  - differential rendering (ANSI swap-chain, only changed cells redrawn);
  - gradient status bar and dividers;
  - scrollable transcript viewport with search/find;
  - input editor with history, tab completions, and bracketed paste;
  - interactive model picker with filtering and keyboard navigation;
  - API key prompt with visibility toggle;
  - slash command integration (`/model`, `/sessions`, `/resume`, `/fork`, etc.);
  - resize detection and 60fps frame pacing;
- core/provider tests.

It does **not** yet implement the full long-term architecture: durable event logs, VFS snapshots, plugin runtime, sandboxing, GUI, remoting, syntax rendering, or WASM extensions.

## Active Planning Sources

The old monolithic design document has been superseded. Current planning lives in:

- [`docs/rfcs/README.md`](docs/rfcs/README.md) — RFC index and architectural north star.
- [`docs/rfcs/IMPLEMENTATION-BASELINE.md`](docs/rfcs/IMPLEMENTATION-BASELINE.md) — what exists in the current codebase.
- [`docs/rfcs/TRACEABILITY.md`](docs/rfcs/TRACEABILITY.md) — mapping from the old seed document to the RFCs.
- [`docs/implementation-plans/0001-core-architecture-groundwork.md`](docs/implementation-plans/0001-core-architecture-groundwork.md) — first implementation plan for preparing the C# core architecture.
- [`docs/implementation-plans/0002-provider-api-abstraction.md`](docs/implementation-plans/0002-provider-api-abstraction.md) — provider architecture for stateless/stateful APIs, including OpenAI Responses.

## Near-Term Direction

The immediate goal is to upgrade the MVP into a clean architectural foundation before building large RFC features.

Near-term work should focus on:

1. moving orchestration out of `Omicron.CLI/Program.cs`;
2. adding core host/registry abstractions;
3. introducing internal C# extension hook points;
4. routing built-in tools through the same extension/tool pipeline future plugins will use;
5. adding permission, workspace, and execution broker seams;
6. hardening sessions and events into durable/replayable shapes;
7. keeping current CLI and TUI behavior working while the core is modularized.

This prepares the codebase for later work such as persistence, GUI frontends, sandboxing, WASM plugins, remoting, and syntax rendering.

## Build and Test

Run tests with:

```bash
dotnet test Omicron.slnx --nologo
```

Run the console MVP with classic CLI mode:

```bash
dotnet run --project Omicron.CLI
```

Run with the rich TUI (default when stdout is a terminal):

```bash
dotnet run --project Omicron.CLI -- --tui
```

## Reference Material

`READ_ONLY/` contains external/reference code snapshots used for design research. These should be treated as read-only references. Any ideas adapted from them should be reimplemented within Omicron's own architecture and documented in the relevant RFC or implementation note.
