# Implementation Plan 0007: Live CLI and Provider Hardening Pass

Status: Proposed  
Depends on: Plans 3, 3.5, 3.8, 4, 4.1  
Primary RFCs: RFC 0001, RFC 0006, provider implementation notes

## Purpose

Run a practical dogfooding and hardening pass against the live CLI, disk persistence, and provider/tool-use flows.

This is not a major architecture plan. It is a focused validation/stabilization pass after large core changes (Plans 3.8, 4, 4.1).

## Current State (Post-Plan-4.1)

New features requiring manual validation:

- `read_path` now has `format` parameter (`auto`, `text`, `hex`, `base64`) with model-aware binary handling.
- Hex dump with byte offset/limit and `0x` hex support for binary file inspection.
- Directory listings show `(binary)` indicator for non-text files via `TextEncodingDetector`.
- `/model` and `/models` show metadata-backed context lengths from `models.dev`.
- 16 content processors (image, PDF, audio, video, Office, CSV, email, archive, ebook, notebook, SVG) with 8 NuGet-backed libraries.
- `ModalityUsedEvent` persisted for cross-model compatibility validation.
- `ForkSessionAsync` / `ResumeSessionAsync` validate modality compatibility before transfer.

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
- `/model`, `/models`, `/model <n|key>` — verify metadata-backed context lengths display correctly
- `/tools`
- `/events [n]`
- `/provider-state`
- `/clear-state`
- `/reset`
- `/exit`, `/quit`

### Binary/file read paths

- `read_path` on a PNG with vision model → base64 inline
- `read_path` on a PNG with text-only model → hex dump
- `read_path` with `format: "hex"` on any binary → hex dump with correct offset
- `read_path` with `format: "base64"` on any file → raw base64
- `read_path` on a directory → shows `(binary)` for non-text files
- Offset/limit with `0x` hex values in format: hex mode
- Large binary file continuation hint displayed correctly

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
