# Implementation Plan 0007: Live CLI and Provider Hardening Pass

Status: Proposed  
Depends on: current CLI MVP, Plan 3 persistence/session foundations  
Primary RFCs: RFC 0001, RFC 0006, provider implementation notes

## Purpose

Run a practical dogfooding and hardening pass against the live CLI, disk persistence, and provider/tool-use flows.

This is not a major architecture plan. It is a focused validation/stabilization pass after large core changes.

## Goals

- Catch real provider/API regressions.
- Validate default disk persistence in normal use.
- Validate tool-use loops with OpenAI/OpenRouter Responses.
- Improve CLI diagnostics where needed.

## Test Matrix

### CLI persistence

- Fresh start creates/uses JSONL store under config dir.
- Session store path is printed.
- Chat creates session record and appends events.
- Restart CLI and verify `/sessions` once Plan 3.5 exists.

### Provider flows

- OpenAI Chat simple prompt.
- OpenAI Responses simple prompt.
- OpenAI/OpenRouter Responses tool call.
- OpenRouter Responses stateless full-context tool-use flow.
- Anthropic simple prompt/tool call if key available.

### Slash commands

- `/help`
- `/status`
- `/model`, `/models`, `/model <n|key>`
- `/tools`
- `/events [n]`
- `/provider-state`
- `/clear-state`
- `/reset`
- `/exit`, `/quit`

### Error/cancel paths

- Escape cancel during streaming.
- Provider API failure displays useful message.
- Tool failure emits/persists error event.
- Reset after tool use clears provider state.

## Deliverables

- Manual test notes under `docs/code-reviews/` or `docs/reports/`.
- Bug fixes discovered during dogfooding.
- Small CLI diagnostic improvements if needed.

## Acceptance Criteria

- Live CLI can complete at least one provider/tool-use loop with disk persistence enabled.
- Events are persisted and readable after restart.
- No known crash on slash-command flows.
- Any provider-specific incompatibility is documented or guarded.
- Build/test pass with no warnings after fixes.

## Non-Goals

- No new large architecture.
- No new provider abstraction unless a live issue proves it necessary.
- No TUI/GUI work.
