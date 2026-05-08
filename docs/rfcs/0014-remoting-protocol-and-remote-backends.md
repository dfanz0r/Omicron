# RFC 0014: Remoting Protocol and Remote Backends

Status: **Research/Planned**

## Purpose

Define Omicron's architecture for remote backends and a transport-neutral binary remoting protocol. The goal is to support VS Code Remote-style workflows, detached remote agents, distributed sub-agents, fleet dashboards, and future web/GUI frontends without treating an interactive SSH shell as the primary control surface.

Omicron should be able to start or attach to an agent runtime on another machine, communicate through a structured API, survive frontend disconnects, and choose the best available transport for the network environment.

## Goals

- Split Omicron into frontend and backend roles with a stable protocol boundary.
- Allow agents to run directly on the host where work occurs.
- Support detached sessions that continue during frontend/network disconnects.
- Support multiple parallel active agents per backend host.
- Address agents, tasks, terminals, and subscriptions explicitly in the protocol.
- Support task delegation to remote sub-agents.
- Support foreground/focus, background/quiet, and multi-pane observation modes.
- Support a dashboard that observes and controls agents across multiple machines.
- Use a binary, transport-neutral protocol suitable for C#, TypeScript, Rust, and future runtimes.
- Support multiplexed channels with priority, flow control, and low-latency cancellation.
- Allow multiple transport bindings: in-process, IPC, TCP, SSH tunnel, WebSocket, QUIC/WebTransport, VPN/mesh, and relay-assisted transports.
- Keep SSH useful for bootstrap and tunneling without requiring the protocol to run over a fragile shell stream.

## Non-Goals

- Replacing SSH as an authentication and provisioning mechanism in the first implementation.
- Requiring public inbound ports on remote machines.
- Requiring a hosted relay/rendezvous service for the initial implementation.
- Defining every final method id, payload schema, or byte offset in this RFC.
- Building a fully custom serialization format before evaluating MessagePack performance and ergonomics.
- Assuming QUIC is available everywhere on day one.

## Architecture Overview

```
Frontend: TUI / GUI / Web / Headless Controller
  ↓
Omicron Remoting Protocol
  ↓
Transport Binding: in-process / IPC / TCP / SSH tunnel / WebSocket / QUIC / relay
  ↓
Backend: sessions / agents / tools / workspace / shell / persistence / policy
```

The frontend owns presentation, input, local commands, and dashboard behavior. The backend owns durable agent execution, workspace access, tool execution, event persistence, policy enforcement, and process/shell management.

Local mode should eventually use the same logical split, even when frontend and backend live in one process. This prevents remote support from becoming a special case.

## Core Concepts

### Frontend

A frontend is any user-facing or automation-facing client:

- TUI
- GUI
- web UI
- CLI controller
- dashboard
- test harness
- another agent delegating work

Frontends render session state, submit user input, attach/detach from sessions, and issue lifecycle commands.

### Backend

A backend is an Omicron runtime responsible for work execution on one host. It provides:

- session lifecycle
- agent lifecycle
- tool execution
- VFS/workspace access
- terminal/process management
- event log persistence
- permission/policy checks
- capability advertisement
- remote task/sub-agent execution

### Session

A session is durable backend state that can outlive a frontend connection. Sessions have stable identifiers and append-only event streams compatible with RFC 0001 and RFC 0006.

A session may contain multiple active agents. A frontend attachment to a session does not imply that every agent in that session is visible, foregrounded, or streamed at full fidelity.

### Agent

An agent is an independently running execution actor hosted by a backend. Agents have stable identifiers and may be local primary agents, delegated sub-agents, review agents, test agents, or long-running background agents.

Each agent belongs to a session and may own or reference tasks, tool calls, terminal/process handles, VFS overlays, subscriptions, and event streams. Protocol operations that affect an agent must address it explicitly by `agentId`; operations that affect the whole session must address `sessionId`; operations that affect a lower-level resource must include the relevant resource id.

### Attachment

An attachment is a frontend's live relationship to a backend/session/agent set. A single frontend can attach to many agents, and multiple frontends may attach to the same agent or session where policy allows.

Attachments carry view intent, such as foreground, background, quiet, summarized, or hidden. This is client interest state, not agent execution state: quieting an agent for one frontend must not stop the agent unless an explicit pause/cancel command is sent.

### Channel

A channel is an independent logical stream within a remoting connection. Channels carry control messages, events, terminal data, file chunks, task updates, or transcript backfill. Channels have identifiers, priorities, flow-control windows, lifecycle state, and optional target ids such as `sessionId`, `agentId`, `taskId`, `terminalId`, or `operationId`.

### Transport Binding

A transport binding maps Omicron remoting connections and channels onto a concrete communication mechanism. The protocol should not assume a specific transport.

## Operation Modes

### Local Split Mode

Frontend and backend run on the same machine, communicating through in-process queues, IPC, or localhost sockets. This validates the architecture before distributed remoting is required.

### Direct Remote Control Mode

A local frontend controls a backend on a remote host. The backend runs tools, edits files, spawns shells, and stores session state on the remote machine.

### Multi-Agent Host Mode

A single backend host may run many agents in parallel. Some agents may be foregrounded in one client, backgrounded in another, visible in a tmux-style multi-pane UI, or not currently viewed by any frontend.

The protocol must distinguish execution state from observation state. Agents continue according to backend policy even when no frontend is actively displaying their full transcript.

### Detached Remote Agent Mode

A frontend starts an agent on a remote backend, disconnects, and later reconnects. The backend continues execution where policy allows. Reattach should use session id plus last-seen event cursor.

### Delegated Sub-Agent Mode

A local or remote agent delegates a bounded task to another backend. Example: run Windows-specific tests on a Windows host while the primary agent continues on Linux.

### Fleet Dashboard Mode

One frontend observes and controls multiple backends. It can show machines, active sessions, active agents, pending tasks, health, reconnect status, resource usage, and attention-worthy notifications from quiet/background agents.

### Shared Attach Mode

Multiple frontends may eventually attach to the same session. This is not required for v0, but the session/event model should not forbid it.

## Protocol Direction

Omicron should define its own lightweight binary remoting protocol rather than adopting JSON-RPC, tRPC, ConnectRPC, or MagicOnion as the core abstraction.

Rationale:

- JSON-RPC is too string-heavy for high-volume interactive streams.
- tRPC is TypeScript-centric and does not fit a C# backend-first architecture.
- ConnectRPC is attractive conceptually, but C# server support is not a good fit for this project direction.
- MagicOnion is attractive for C#, but lacks the JavaScript/browser ecosystem needed for future frontends.
- A compact binary protocol over a small set of primitives is easier to map to many transports.

MessagePack is the preferred initial payload encoding. It provides compact binary encoding, good C# support, good TypeScript support, and extensible maps/arrays without committing to a fully custom binary serializer.

## Framing Model

MessagePack does not provide stream framing. Ordered byte-stream transports need an Omicron frame header followed by a MessagePack payload.

A provisional frame shape:

```text
FrameHeader
  magic              fixed bytes identifying Omicron remoting
  protocolVersion    major/minor or negotiated version
  frameType          request / response / event / channel-data / control
  flags              compression, final, error, etc.
  messageId          request/response correlation id
  channelId          logical channel id
  targetKind         optional target namespace: backend/session/agent/task/terminal/operation
  targetId           optional target id within that namespace
  payloadLength      number of following payload bytes

Payload
  MessagePack encoded body
```

Exact sizes and numeric values are deferred to the implementation design, but the protocol must support:

- request/response RPC
- unsolicited events
- streaming chunks
- channel open/close
- ping/pong/health checks
- cancellation/interruption
- error reporting
- capability negotiation
- version negotiation
- target addressing for backend/session/agent/task/terminal/operation scoped messages
- subscription and attention-state changes

## Method and Message Identity

Hot-path protocol operations should use numeric ids rather than method-name strings.

Example reserved ranges:

```text
0-999       core protocol
1000-9999   official Omicron extensions
10000+      custom/vendor/plugin extensions
```

Debug tooling may display symbolic names, but the wire format should not require string dispatch for core operations.

## Channels and Multiplexing

A connection may carry many channels:

```text
connection
  channel 0: protocol/control
  channel 1: session events for session abc
  channel 2: foreground transcript for agent a1
  channel 3: terminal output/input for agent a1 terminal t1
  channel 4: summarized/background events for agent a2
  channel 5: file transfer for operation op9
  channel 6: delegated sub-agent task for agent a3
```

Channels are required early because Omicron must support concurrent activity over one authenticated connection. However, naive multiplexing over one byte stream is not enough. High-volume channels must not significantly delay low-latency operations.

## Agent Addressing and Attention Model

The protocol must support multiple active agents per backend host and per session. A frontend may observe one agent, many agents, or only dashboard-level summaries.

### Target Addressing

Requests, responses, events, and stream chunks should include enough addressing information for the receiver to route them without global ambiguity.

Common target scopes:

```text
backendId            host/runtime-wide operations
sessionId            session-level state and event stream
agentId              agent transcript, lifecycle, input, controls
taskId               delegated or scheduled unit of work
operationId          long-running tool/file/model operation
terminalId           PTY/process stream owned by an agent or session
workspaceId          workspace/VFS root or overlay
```

The protocol should avoid relying on "currently selected agent" as implicit state for commands. UI selection may change locally; protocol commands should name their targets explicitly.

### Attachment and Subscription State

A frontend should communicate what it wants to observe through subscriptions:

```text
AttachBackend(backendId)
AttachSession(sessionId, lastSeenEventSeq)
SubscribeAgent(agentId, mode, lastSeenEventSeq)
UnsubscribeAgent(agentId)
SetAgentAttention(agentId, attentionMode)
SubscribeDashboard(filter, summaryLevel)
```

Provisional attention modes:

```text
foreground       stream full transcript/events with low latency
visible          stream enough for a pane/card that is visible but not primary
background       stream important state changes and bounded summaries
quiet            suppress noisy transcript/tool output; allow notifications
hidden           no live stream except critical policy/health notifications
```

Attention mode is per attachment/frontend. It is not an execution command. To affect execution, clients must send explicit `PauseAgent`, `ResumeAgent`, `CancelAgent`, `InterruptTerminal`, or task-control messages.

### Notification Escalation

Quiet/background agents still need a way to notify users. Events should carry notification severity and delivery hints independent of transcript streaming:

```text
silent           persist only; no immediate UI surfacing
badge            update dashboard count/status
toast            notify user non-modally
attention        request user focus
blocking         requires approval/input before continuing
critical         security, data-loss, crash, or policy issue
```

Examples that may escalate from quiet agents:

- approval required
- task completed or failed
- test result changed from pass to fail
- merge conflict or edit failure
- credential/auth prompt
- sandbox/policy violation
- backend health degradation
- agent crash

The backend should persist all authoritative events, but live delivery may be filtered by subscription and attention mode. A frontend can later request replay/backfill for an agent when the user foregrounds it.

### Multi-Pane / Tmux-Style Observation

A frontend may subscribe to multiple agents in `foreground` or `visible` mode at once. The protocol must not assume exactly one active agent per client. Flow control and priority scheduling still apply per channel so one pane's terminal spam cannot starve another pane's controls or notifications.

### Foreground Transitions

When a frontend foregrounds an agent after it was quiet/backgrounded, the backend should support efficient catch-up:

```text
SetAgentAttention(agentId, foreground)
BackfillAgent(agentId, fromSeq or time/window policy)
Open transcript/tool/terminal channels as needed
Resume live full-fidelity stream
```

Backfill policy may request all missed events, recent tail, summarized history plus tail, or only a snapshot. Exact retention depends on RFC 0006 persistence policy.

## Head-of-Line Blocking Avoidance

The protocol must explicitly address head-of-line blocking.

Failure case to avoid:

```text
terminal spam fills the outbound queue
cancel request waits behind megabytes of terminal output
user cannot stop the command producing the spam
```

Requirements:

- per-channel queues
- per-channel flow control
- connection-level flow control
- priority-aware scheduling
- bounded frame sizes
- bounded write-ahead into OS/transport buffers
- backpressure to producers
- optional lossy/coalescing behavior for noisy streams
- emergency control traffic that can bypass bulk queues

On ordered byte streams such as TCP, SSH tunnels, and WebSocket-over-TCP, Omicron cannot undo head-of-line blocking once bytes have already entered the transport. Implementations must therefore keep underlying transport buffers shallow and perform scheduling before writing bytes.

### Priority Classes

Provisional priority classes:

```text
0 emergency/control
  cancel, interrupt, close, ping/pong, auth, session control

1 interactive
  user input, terminal input, UI steering, agent steering

2 normal
  agent events, RPC responses, metadata, ordinary tool updates

3 bulk
  file transfers, transcript backfill, logs, terminal output floods
```

The writer should service higher-priority queues first while preserving enough fairness that lower-priority work eventually progresses.

### Flow Control

Channels should use credit/window based flow control:

```text
ChannelOpen(channelId, kind, priority, initialWindow)
ChannelData(channelId, seq, payload)
ChannelWindowUpdate(channelId, additionalBytes)
ChannelPause(channelId)
ChannelResume(channelId)
ChannelClose(channelId, reason)
```

A sender must not exceed the receiver's advertised channel window. Bulk producers must be throttled before they consume unbounded memory.

### Lossy or Coalescing Channels

Some channels can degrade under load. Terminal output, telemetry, and progress updates may support policies such as:

- coalesce adjacent chunks
- drop middle content
- emit an explicit truncation/dropped-output event
- preserve final state or recent tail

Lossy behavior must be opt-in per channel kind. File transfer, edits, and protocol control are never lossy.

## Transport Bindings

The core protocol defines sessions, messages, channels, capabilities, and semantics. Transport bindings define how those abstractions move over a link.

### In-Process Binding

Uses memory queues/channels. Useful for tests and local split mode.

### IPC Binding

Uses named pipes, Unix domain sockets, or platform IPC. Useful for splitting frontend/backend processes on one host without exposing network ports.

### TCP Binding

Uses framed protocol over a TCP socket. Suitable for localhost, VPN, mesh networks, or explicitly reachable hosts.

### WebSocket Binding

Useful for browser/web UI compatibility and environments where WebSocket infrastructure is easier than raw TCP.

### SSH Tunnel Binding

Preferred initial SSH remote mode:

```text
local frontend -> localhost random port -> SSH -L -> remote 127.0.0.1 backend port
```

The backend listens only on loopback. No remote firewall hole is required, and the binary protocol does not share stdout/stderr with shell text.

### SSH stdio Binding

SSH stdio is possible with `ssh -T`, but it is fragile for a binary protocol because stdout contamination can come from accidental logs, dependencies, crash dumps, shell wrappers, or runtime diagnostics.

It should be treated as constrained fallback or bootstrap/debug mode, not the preferred production transport. If implemented:

- no PTY may be allocated
- stdout must contain protocol bytes only
- stderr is diagnostics only
- an initial magic/version handshake must detect contamination
- the connection must fail closed on unexpected bytes

### QUIC / WebTransport Binding

QUIC is an excellent long-term fit because it provides native multiplexed streams, independent stream flow control, connection-level flow control, TLS, and reduced head-of-line blocking compared to TCP.

A QUIC binding should map Omicron channels to QUIC streams where possible:

```text
QUIC connection
  stream: control
  stream: session events
  stream: terminal
  stream: file transfer
  stream: transcript backfill
```

QUIC datagrams may later carry opt-in lossy telemetry/progress data.

The core protocol should be QUIC-friendly from day one, but QUIC is not required for v0 because platform, deployment, and JavaScript/browser support need validation.

## SSH Bootstrap and Lifecycle

SSH remains very useful for provisioning, authentication, and creating a private tunnel.

Preferred SSH bootstrap flow:

```text
1. Frontend connects to user@host over SSH.
2. It checks for a compatible Omicron remote runtime.
3. If missing or incompatible, it uploads/downloads/installs one under ~/.omicron/remote/<version>.
4. It starts or discovers a backend daemon bound to 127.0.0.1:0.
5. Backend writes connection metadata to a controlled file under ~/.omicron/run/.
6. Frontend retrieves metadata and opens an SSH local port forward.
7. Frontend connects to localhost forwarded port.
8. Binary remoting handshake begins over TCP through the SSH tunnel.
```

Metadata should include:

```text
backend id
process id
listen address/port
protocol version
capabilities
short-lived connection token or public-key challenge data
expiration time
```

The protocol should avoid depending on parsing arbitrary remote stdout for long-lived communication.

## Connectivity and Firewall Traversal

Omicron must not assume either side has inbound public reachability.

Connection strategy ladder:

```text
1. in-process/local IPC
2. SSH bootstrap + SSH local port forwarding
3. SSH reverse forwarding when useful
4. direct TCP/WebSocket over reachable networks
5. VPN/mesh networks such as WireGuard, Tailscale, Headscale, ZeroTier, or corporate VPN
6. QUIC with rendezvous/hole punching
7. relay-assisted transport
8. SSH stdio fallback for constrained environments
```

### Rendezvous and Hole Punching

For non-SSH distributed agents, Omicron may eventually use a rendezvous service:

```text
1. both peers authenticate independently
2. both connect outbound to rendezvous
3. candidates are exchanged
4. direct QUIC/UDP connectivity is attempted
5. if direct fails, fall back to relay or another transport
```

This resembles ICE/STUN/TURN-style establishment. It is useful but not required for v0.

### Relay

A relay allows both sides to make outbound connections:

```text
frontend -> relay <- backend
```

Relays should be optional and self-hostable. They should not be trusted with plaintext session contents. End-to-end authentication/encryption must be between frontend and backend, with the relay forwarding opaque bytes.

## Model Provider Credentials and Delegated Grants

Remote agents need model-provider access, but remote backends should not automatically receive the user's permanent provider secrets. Omicron should support a locked, platform-independent encrypted credential vault and explicit remote credential grant flows.

### Credential Vault Principle

Omicron's canonical provider credentials should live in an Omicron-owned encrypted vault, not directly in platform-specific keychains. The vault is locked by default. A user passphrase, local unlock key, or future hardware/OS-assisted mechanism unlocks the vault for an authenticated Omicron runtime session.

While unlocked, decryption material exists only in memory. A backend started independently without a valid unlock session can see only encrypted credential records and non-secret configuration; it cannot call linked model providers or resume secret-dependent detached agents.

OS keychains may later cache or protect local unlock keys, but the portable vault remains the source of truth so remote sync/delegation uses one model across Windows, macOS, Linux, headless servers, and containers.

### Remote Credential Modes

A remote backend may obtain model-provider access through one of several explicit modes:

```text
frontendProxy:
  backend asks the connected frontend/model proxy to perform provider calls
  provider secrets remain local
  detached execution depends on the proxy staying available

backendLocal:
  backend has its own independently configured provider credential
  best for long-running detached hosts but requires remote secret management

temporaryDelegatedGrant:
  frontend unlocks selected credentials locally and delegates a scoped, short-lived grant
  backend can call providers directly until grant expiry/revocation

persistentBackendGrant:
  selected credential records or record keys are wrapped to the backend identity
  requires explicit user approval and stronger revocation/audit UX

modelGateway:
  backend calls a trusted Omicron gateway that owns provider credentials and policy
  useful for fleets and enterprise deployments
```

The default SSH remote mode should be `frontendProxy` or a user-approved `temporaryDelegatedGrant`, not silent persistent secret sync.

### Temporary Delegated Credential Grants

For detached or low-latency remote work, the preferred first direct-provider mode is a temporary delegated grant.

Flow:

```text
1. Frontend unlocks the local credential vault.
2. Remote backend proves its BackendId over the authenticated remoting connection.
3. User approves a scoped grant for a provider/account/session/agent/task.
4. Frontend decrypts only the selected provider credential or record key locally.
5. Frontend sends a grant envelope to the backend over the encrypted remoting channel.
6. Backend keeps the usable secret or record key in memory only.
7. Backend enforces grant policy before each provider call.
8. Backend discards the grant on expiry, revocation, lock, shutdown, or policy violation.
```

Conceptual grant payload:

```text
DelegatedProviderGrant
  grantId
  providerId              openai, anthropic, openrouter, ollama-cloud, etc.
  accountId               user/provider account label or id
  backendId               authorized backend identity
  sessionId?              optional session scope
  agentId?                optional agent scope
  taskId?                 optional task scope
  allowedModels[]         model allowlist
  allowedProviderApis[]   chat, responses, embeddings, etc.
  maxTokens?              budget guardrail
  maxCost?                budget guardrail where estimable
  expiresAt               required for temporary grants
  issuedAt
  issuedByFrontendId
  auditLabel
  encryptedSecretOrRecordKey
```

Temporary grants are not provider-native security boundaries unless the provider itself supports scoped ephemeral tokens. They are Omicron-enforced policy around a secret the backend can use while the grant is live. Therefore:

- grants should be short-lived by default;
- grants should be scoped as narrowly as practical;
- grants should be auditable;
- grants should be revocable;
- high-security users should prefer `frontendProxy` or `modelGateway`;
- persistent backend grants require explicit opt-in.

### Locking and Detached Agents

Remote detach behavior depends on credential mode:

```text
frontendProxy:
  detached agent pauses or fails when provider calls are needed and proxy is gone

temporaryDelegatedGrant:
  detached agent may continue until grant expiry or budget exhaustion

backendLocal / persistentBackendGrant / modelGateway:
  detached agent may continue according to backend policy and provider availability
```

When a grant expires while an agent is running, the backend should raise an attention-worthy notification and transition the agent to a waiting-for-credentials state rather than silently failing if recovery is possible.

## Security Model

Security must be explicit because the backend can run tools, read files, spawn processes, and access credentials.

Requirements:

- backend binds to loopback by default unless explicitly configured otherwise
- all remote connections authenticate
- SSH bootstrap establishes an initial trust path when used
- upgraded transports prove possession of a short-lived token or key exchanged during bootstrap
- backend advertises capabilities and policy before accepting sensitive commands
- frontend verifies backend identity/version/capabilities
- session attach requires authorization
- permissions remain enforced by the backend, not just the frontend
- relay/rendezvous services must not be trusted with plaintext
- remote runtime downloads/updates must be integrity checked
- provider credentials must not be silently copied to remote backends
- temporary delegated provider grants must be scoped, expiring, auditable, and revocable
- a backend started without unlock material must not be able to decrypt linked provider credentials

Open security questions:

- What exact encrypted vault format and crypto library should be standardized?
- Should temporary grants carry decrypted provider secrets, selected record keys, or provider-native ephemeral tokens where available?
- How are multiple frontend identities represented?
- What is the default session attach policy on shared hosts?

## Persistence, Reconnect, and Replay

Remote backends should own durable session state. A frontend may disconnect without killing running work unless policy says otherwise.

Backend state should include:

- sessions
- agents
- tasks
- frontend attachments and subscription preferences where useful
- attention/quiet state per attachment
- operation handles
- active delegated credential grant metadata/handles, but not persisted plaintext secrets
- event logs
- event sequence cursors per session and agent
- active process/terminal handles
- workspace roots and VFS overlays

Reconnect flow:

```text
AttachSession(sessionId, lastSeenEventSeq)
  -> session snapshot
  -> active agent inventory
  -> replay session events after cursor
  -> subscribe/foreground selected agents
  -> live event streams resume according to subscription filters
```

Event ordering and persistence should align with RFC 0001 and RFC 0006.

Agent-level event streams may have their own sequence cursors in addition to the session-level cursor. Session snapshots should include enough agent inventory to let a reconnecting frontend decide which agents to foreground, show as visible panes, keep quiet, or ignore.

## Relationship to Other RFCs

- RFC 0001 defines the core event model that remote sessions must preserve.
- RFC 0002 defines commands, permissions, and semantic UI that remote frontends consume.
- RFC 0005 defines embedded terminal panes; terminal channels must support their high-volume streams and cancellation.
- RFC 0006 defines persistence and snapshots; remote sessions depend on durable event logs.
- RFC 0007 defines sandbox/execution policy that remote backends enforce.
- RFC 0008 defines GUI frontend needs that benefit from the frontend/backend split.
- RFC 0010 defines WASM plugin runtime constraints; plugins must be target-scoped and transport-blind when interacting with remote/multi-agent state.
- RFC 0012 defines sub-agent orchestration; remote backends are natural sub-agent execution targets.

## Implementation Plan

### Phase 0: Protocol Spike

- Define minimal frame header and MessagePack envelopes.
- Implement in-process transport.
- Implement request/response, events, channel open/close, cancellation, ping/pong.
- Add explicit target addressing for backend/session/agent/task/terminal operations.
- Add basic agent subscription and attention-mode messages.
- Add tests for framing, malformed frames, version mismatch, cancellation priority, and multi-agent routing.

### Phase 1: Local Backend Process

- Split a backend process from the current CLI/TUI path.
- Add IPC or localhost TCP transport.
- Run local sessions through the remoting boundary.
- Validate persistence/replay through reconnect.

### Phase 2: SSH Tunnel Remote

- Add SSH bootstrap commands.
- Install/discover remote backend under `~/.omicron/remote/<version>`.
- Start backend bound to `127.0.0.1`.
- Create SSH local port forward.
- Connect using the binary protocol through the tunnel.
- Add locked-backend behavior when no provider credential grant/proxy is available.

### Phase 3: Remote Detach/Reattach

- Persist remote sessions.
- Add reattach by session id, agent inventory, and event cursors.
- Add foreground/background/quiet subscription modes.
- Add temporary delegated provider grants for selected providers/accounts/models.
- Add grant expiry/revocation/waiting-for-credentials behavior.
- Add graceful frontend disconnect handling.
- Add remote cancellation and interrupt semantics.

### Phase 4: Delegated Sub-Agents and Dashboard

- Add remote task submission.
- Surface backend inventory, active agents, and health.
- Support multiple backend connections in one frontend.
- Support tmux-style observation of multiple agents at once.

### Phase 5: Advanced Connectivity

- Evaluate direct TCP/WebSocket, QUIC/WebTransport, VPN/mesh discovery, rendezvous, and relay-assisted modes.

## Tests and Benchmarks

Required tests:

- frame parser rejects malformed/oversized frames
- MessagePack payload compatibility across C# and TypeScript
- high-volume bulk channel cannot starve cancellation
- one agent's noisy transcript/terminal cannot starve controls or notifications for another agent
- per-channel flow control prevents unbounded memory growth
- multi-agent target ids route commands/events to the correct agent
- foreground/background/quiet subscriptions filter live delivery without stopping execution
- reconnect replays exactly the missing event range
- SSH tunnel transport works without exposing remote ports
- stdout contamination is detected if stdio fallback is enabled
- backend rejects unauthenticated attach/start commands
- backend cannot use linked provider credentials when started without unlock material
- temporary delegated grants are scoped to the intended backend/session/agent/task
- expired/revoked grants prevent provider calls and raise attention-worthy agent state

Benchmarks:

- request/response latency
- event throughput
- terminal spam with concurrent cancel latency
- multi-agent terminal/transcript spam with concurrent cross-agent controls
- file-transfer throughput under active control traffic
- foreground transition catch-up/backfill latency
- reconnect replay time for large sessions
- memory usage under backpressure

## Open Questions

- Should MessagePack payloads use array-based compact records, map-based extensible records, or a hybrid?
- What is the exact frame header layout and endianness?
- Should compression be per-frame, per-channel, or negotiated by channel kind?
- Should agent event cursors be globally session-ordered, per-agent ordered, or both?
- What are the default notification escalation rules for quiet/background agents?
- What is the first TypeScript protocol implementation target: Node, browser, or both?
- Which C# MessagePack library should be standardized?
- Should QUIC be implemented through `System.Net.Quic`, MsQuic directly, a Rust sidecar, or deferred entirely?
- How should the credential vault represent provider-native OAuth flows and refresh tokens?
- How much remote runtime management should Omicron own versus delegating to system packages/containers?

## Design Decisions

1. Remote support is framed as **remote backends**, not merely Remote SSH.
2. SSH is preferred for bootstrap and tunneling, not as the primary binary stdio transport.
3. The protocol is transport-neutral and binary.
4. MessagePack is the preferred initial payload encoding.
5. Channels are first-class from the beginning.
6. Agents are explicit protocol targets; the protocol must support multiple active agents per backend/session.
7. Foreground/background/quiet state is frontend attachment state, not execution state.
8. Priority scheduling and flow control are required to preserve cancellation responsiveness.
9. QUIC is a preferred future transport shape, but not a v0 dependency.
10. Backends own durable session state so agents can survive frontend disconnects.
11. Remote provider credentials use explicit modes. The preferred direct-provider mode is a scoped, short-lived delegated grant; persistent remote secret sync is opt-in only.
