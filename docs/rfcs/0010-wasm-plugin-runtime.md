# RFC 0010: WebAssembly Plugin Runtime

Status: **Research/Planned**

## Purpose

Define Omicron's preferred long-term plugin execution model. Plugins are more dangerous than LLM-generated commands because they are persistent, user-installed code with access to high-value Omicron APIs. Omicron should therefore treat plugins as untrusted by default and run them behind a WebAssembly capability boundary where practical.

The target server-side runtime is Wasmtime. AssemblyScript is the preferred first-class authoring target because it can produce small WASM modules and has a direct host-bindings story. Other languages may be supported later, including C# via Blazor WebAssembly AOT, Rust, TinyGo, or any language that can satisfy the Omicron plugin ABI.

## Goals

- Run third-party plugins with less ambient authority than native process plugins.
- Make plugin capabilities explicit and reviewable.
- Allow plugins to register tools, commands, panels, status items, providers, and event handlers through host bindings.
- Route all filesystem, network, shell, model, UI, remoting, and persistence access through Omicron host APIs.
- Support remote-backend and multi-agent aware host contexts without giving plugins direct transport authority.
- Keep a path open for browser-side or fully virtual in-browser Omicron environments.
- Support multiple plugin languages without redefining the host plugin model for each language.

## Non-Goals

- Guarantee perfect isolation against all runtime or JIT/compiler bugs.
- Run arbitrary Node/NPM plugin code inside the WASM runtime without porting.
- Make browser-side plugins a near-term requirement.
- Replace the separate command/code sandbox from RFC 0007. Plugin sandboxing and LLM execution sandboxing are related but distinct layers.

## Security Principle

> Plugins get no ambient authority. They receive only imported host functions and handles that Omicron explicitly grants.

A WASM plugin should not receive direct host filesystem, process, network, credential, terminal, GUI, or remoting-transport access. It asks Omicron to do things through capability-checked host calls.

## Runtime Architecture

```text
plugin package
  ↓
manifest + wasm module(s)
  ↓
Omicron plugin loader
  ↓
capability review + policy
  ↓
Wasmtime instance/store
  ↓
Omicron host bindings
  ↓
core services: events, tools, commands, semantic UI, VFS, persistence, model providers
```

Recommended packages:

```text
Omicron.Plugins.Abstractions
  plugin manifest model
  capability declarations
  host ABI contracts
  semantic registration records

Omicron.Plugins.Wasm
  Wasmtime host
  module loader
  instance lifecycle
  memory/resource limits
  host bindings implementation
  plugin package validation

Omicron.Plugins.AssemblyScriptSdk
  AssemblyScript bindings/generator/examples

Omicron.Plugins.DotNetSdk
  optional future C# WASM/AOT bindings
```

## Manifest

Each WASM plugin should declare identity, version, entry module, ABI version, and requested capabilities.

```json
{
  "id": "com.example.git-tools",
  "name": "Git Tools",
  "version": "0.1.0",
  "abi": "omicron-plugin-v1",
  "entry": "plugin.wasm",
  "runtime": "wasm-component",
  "capabilities": {
    "events": ["session_start", "tool_call", "tool_result"],
    "eventScopes": ["session", "agent"],
    "tools": ["git_status", "git_checkpoint"],
    "commands": ["git.checkpoint"],
    "workspace": {
      "read": ["**/*"],
      "write": []
    },
    "network": [],
    "process": [],
    "remoteTargets": ["current_backend"],
    "models": false,
    "ui": ["notify", "semantic_panels"]
  }
}
```

Manifests should be user-reviewable and persisted with plugin installation metadata.

## Host Bindings

AssemblyScript host bindings are a good initial target. The ABI should be narrow and stable. Plugins should communicate with the host using handles and serialized payloads rather than receiving arbitrary object references.

Representative host calls:

```text
register_tool(definition_json) -> result
register_command(definition_json) -> result
register_panel(definition_json) -> result
subscribe_event(event_name, scope_json) -> result
publish_notification(notification_json) -> result
append_plugin_entry(type, data_json) -> result
workspace_read_file(path_handle) -> bytes_handle
workspace_write_file(path_handle, bytes_handle) -> result
request_permission(permission_json) -> decision_handle
```

Host-to-plugin calls:

```text
plugin_init(context_json_with_backend_session_agent_handles) -> result
plugin_on_event(event_json) -> result_json
plugin_execute_tool(tool_call_json) -> tool_result_json
plugin_execute_command(command_json) -> result_json
plugin_render_panel(panel_context_json) -> ui_node_json
plugin_shutdown(reason_json) -> result
```

The first implementation can use JSON payloads for simplicity. A later version can move hot paths to a schema-based binary encoding or the WebAssembly Component Model/WIT.

Host contexts and callbacks must include explicit target handles where relevant (`backendId`, `sessionId`, `agentId`, `taskId`, `workspaceId`, `terminalId`). Plugins must not infer authority from the currently selected frontend agent. This aligns plugin APIs with RFC 0014 remoting target addressing.

## Capability Model

Capabilities are explicit and deny-by-default.

```text
workspace:
  no direct host paths
  read/write via Omicron VFS only
  write through transactions/overlays

network:
  denied by default
  host-mediated fetch only if granted

remoting:
  denied by default
  no direct sockets, SSH, QUIC, relay, or backend transport access
  host-mediated remote target operations only if granted

process:
  denied by default
  host-mediated execution only through RFC 0007 broker

secrets:
  denied by default
  named secret handles only if granted

models:
  no direct provider API keys
  host-mediated model calls only if granted

ui:
  semantic UI by default
  frontend-specific UI only for explicitly trusted/frontend plugins
  remote/background agent notifications must use host notification severity/delivery hints

persistence:
  plugin-scoped key/value or append-only entries only
```

## Resource Limits

Wasmtime-hosted plugins should run with limits:

- memory limit per instance
- fuel/epoch interruption for CPU bounds
- wall-clock timeout for calls
- bounded host-call payload sizes
- bounded persisted plugin state
- bounded output and tool-result sizes
- cancellation through host-controlled interrupts

Long-running work should be modeled as cancellable host-mediated tasks rather than unbounded WASM execution.

## Remote Backend Placement

WASM plugins normally run in the backend that owns the workspace/session/agent they operate on. A frontend connected to a remote backend should not execute a workspace plugin locally unless the plugin is explicitly frontend-only and has no backend authority.

Implications:

- backend plugins see backend-local VFS, sandbox broker, model/provider policy, and agent events through capability handles
- frontend plugins can render local UI and issue host-mediated commands, but cannot bypass backend permissions
- coordinating plugins that act across multiple backends require explicit multi-backend capability grants
- plugin event delivery should respect RFC 0014 subscription, attention, flow-control, and notification escalation rules
- plugin persistent state should be scoped by plugin id plus backend/session/workspace where appropriate

## Server-Side Runtime

Wasmtime is the preferred server-side runtime because it provides mature embedding, resource controls, WASI support where needed, and a path toward the component model.

Default server-side policy:

```text
WASI disabled unless explicitly needed
no preopened directories by default
no inherited stdio by default
no network from WASM runtime itself
no remoting transport access from WASM runtime itself
host APIs are the only authority boundary
```

If WASI is used, it should be heavily restricted and still subordinate to Omicron's capability policy.

## Browser Possibilities

The same or similar plugin ABI could run in a browser environment later:

```text
browser Omicron shell
  ↓
WebAssembly plugin module
  ↓
browser host bindings
  ↓
virtual workspace / IndexedDB / OPFS
  ↓
virtual tools or remote execution broker
```

This opens possibilities for:

- browser-side semantic UI plugins
- fully virtual demo/tutorial environments
- local-only plugins with no server execution
- shared plugin packages across desktop/server/browser with capability differences

This is not a near-term requirement. Most plugins will likely run server-side, but the WASM boundary keeps the option open.

## Language Targets

### AssemblyScript

Preferred initial plugin language:

- TypeScript-like syntax
- small WASM output
- straightforward host bindings
- good fit for semantic tools, event handlers, and UI-node generation

### C# / .NET WASM AOT

Possible future plugin language:

- familiar for Omicron's .NET ecosystem
- can share DTOs and tooling concepts
- likely larger output than AssemblyScript
- startup/memory profile must be benchmarked
- useful for complex enterprise plugins if overhead is acceptable

### Other Languages

Rust, TinyGo, or other WASM-capable languages can be supported if they implement the ABI and packaging rules.

## Relationship to Native/Internal Plugins

Omicron may still have internal trusted plugins compiled into the application or loaded as native .NET assemblies during development. Third-party/distributed plugins should prefer WASM.

Suggested trust tiers:

```text
builtin:
  compiled with Omicron, full internal API as needed

trusted-native:
  local development/enterprise only, explicit unsafe mode

wasm:
  default third-party plugin format, capability-limited

frontend-specific:
  explicitly marked and separately reviewed
```

## Relationship to RFC 0007 Sandboxing

Plugin sandboxing is not the same as sandboxed tool/code execution.

```text
plugin sandbox:
  protects Omicron from installed extension code
  restricts access to Omicron APIs and host resources
  long-lived, event-driven, capability-based

execution sandbox:
  protects host/workspace during command/code execution
  runs tools, shell commands, generated code, tests, etc.
  short-lived or process/session-based provider layer
```

A WASM plugin that wants to run a command must call the Omicron execution broker from RFC 0007. It should not spawn processes directly.

## Design Decisions

1. WASM is the preferred default for third-party plugins.
2. Wasmtime is the preferred server-side runtime.
3. AssemblyScript is the preferred first authoring target.
4. C# WASM AOT is possible but must be benchmarked for size/startup/memory.
5. Plugin authority is expressed through capabilities and host bindings.
6. Browser-side plugin execution remains a future option, not an MVP dependency.
7. Native plugins are reserved for built-ins, development, or explicitly trusted deployments.
