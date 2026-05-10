# Implementation Plan 0006: Provider and Session Replay Hardening

Status: Proposed  
Depends on: Plan 3 persistence/projection, Plan 3.5 session UX  
Primary RFCs: RFC 0001, RFC 0006

## Purpose

Strengthen replay, resume, and provider-state semantics after session UX exists, especially across provider/model boundaries.

## Goals

- Make session resume/fork behavior predictable and safe.
- Avoid accidental provider-managed continuation across incompatible models/providers.
- Improve replay projection coverage as event types grow.

## Work Items

### 1. Projection completeness audit

Review `SessionProjector` against all concrete `OmicronEvent` types.

Add tests to ensure new event types are either projected or intentionally ignored.

Related hardening:

```text
FH-0003: JSONL event deserialization switch requires manual maintenance
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
- OpenRouter Responses remains stateless full-context by default;
- transcript replay is preferred over provider state continuation for transfer.

### 4. Session lineage metadata

If forks are implemented, add metadata/events for:

- source session id;
- fork time;
- target provider/model;
- whether provider state was preserved or cleared.

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
