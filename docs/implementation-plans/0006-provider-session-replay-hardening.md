# Implementation Plan 0006: Provider and Session Replay Hardening

Status: Proposed  
Depends on: Plans 3, 3.5, 4.1 (modality validation gate)  
Primary RFCs: RFC 0001, RFC 0006

## Purpose

Strengthen replay, resume, and provider-state semantics after session UX exists, especially across provider/model boundaries.

## Current State (Post-Plan-4.1)

Already in place:

- `SessionProjector` projects events into `SessionProjection` for replay.
- `ResumeSessionAsync` / `ForkSessionAsync` create sessions from persisted events.
- `ModalityUsedEvent` (Plan 4.1) is emitted during reads and persisted in the session log.
- `CheckModalityCompatibility` gates cross-model fork/resume by validating the target model supports all modalities used in the session.
- `SessionForkRequest` / `SessionResumeRequest` carry model, API key, and system prompt overrides.
- Provider state is restored on same-model resume, cleared on cross-model fork.

## Goals

- Make session resume/fork behavior predictable and safe.
- Avoid accidental provider-managed continuation across incompatible models/providers.
- Improve replay projection coverage as event types grow.

## Work Items

### 1. Projection completeness audit

Review `SessionProjector` against all concrete `OmicronEvent` types, including new types added since Plan 3:

- `ModalityUsedEvent` (Plan 4.1) — should be intentionally ignored by projection (it's metadata, not conversational content)
- Transaction lifecycle events (`TransactionStartedEvent`, `TransactionStagedEvent`, `TransactionCommittedEvent`, `TransactionRolledBackEvent`) — should be ignored

Add tests to ensure new event types are either projected or intentionally ignored.

Related hardening:

```text
FH-0003: JSONL event deserialization switch requires manual maintenance (resolved via OmicronEventRegistry)
```

### 2. AgentSession hydration hardening

Ensure resumed/forked sessions can hydrate:

- transcript messages;
- tool result messages;
- provider state where safe;
- token usage/debug metadata where useful.

### 3. Provider state transfer policy

Codify rules:

- same session + same provider/model/API shape may restore safe provider state;
- cross-model/provider fork must clear provider-managed continuation;
- **Modality validation gate** (Plan 4.1 Phase 6): cross-model fork/resume now validates target model supports all modalities used in the source session before transfer. If incompatible, the operation fails before any state mutation;
- OpenRouter Responses remains stateless full-context by default;
- transcript replay is preferred over provider state continuation for transfer.

### 4. Session lineage metadata

If forks are implemented, add metadata/events for:

- source session id;
- fork time;
- target provider/model;
- whether provider state was preserved or cleared;
- **Modality tracking**: `ModalityUsedEvent` (Plan 4.1) already tracks which modalities were used in the session and which files triggered them — this serves as per-file lineage for cross-model compatibility auditing.

### 5. Replay/provider tests

Add tests for:

- same-model resume provider state restore;
- cross-model fork provider state cleared;
- reset projection clears state;
- tool-call transcript resumes/forks correctly;
- unknown/ignored events do not crash projection.

## Acceptance Criteria

- Resume/fork provider-state behavior is explicit and tested.
- Projection coverage has a guard against silently missing new event types.
- Cross-model movement uses transcript replay only.
- OpenRouter Responses stateless default remains intact.
- Build/test pass with no warnings.

## Non-Goals

- No UI branch graph.
- No provider-managed cross-model continuation.
- No long-term session summarization yet.
