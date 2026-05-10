# Code Review 0090: Plan 0006 — Provider and Session Replay Hardening

**Status:** Accepted — 0 findings  
**Date:** 2026-05-10  
**Build:** 0 errors, 546 tests (+14), 0 failures

---

## What Was Built

| File | Change |
|------|--------|
| `Omicron.Core/Sessions/SessionProjection.cs` | Added comment listing 9 intentionally-ignored event types in the HandleEvent loop |
| `Omicron.Core.Tests/SessionReplayHardeningTests.cs` | 14 tests across all 5 work items |

## Work Item Coverage

| # | Work Item | Tests | Status |
|---|-----------|-------|:------:|
| 1 | Projection completeness audit | `Projection_CoversAllRegisteredEventTypes` — enumerates `OmicronEventRegistry.AllTypes`, asserts 10 handled + 10 ignored = all covered. `Projection_ModalityUsedEvent_IsIgnored`, `Projection_TransactionEvents_AreIgnored`, `Projection_SessionEndedEvent_IsIgnored`, `Projection_ExecutionEvents_AreIgnored`, `Projection_PermissionRequestedEvent_IsIgnored` — 6 tests total | ✅ |
| 2 | AgentSession hydration | `FromProjection_EmptyProjection_HydratesCleanSession`, `FromProjection_SingleUserMessage_RestoresTranscript` — 2 tests | ✅ |
| 3 | Provider state transfer | `FromProjection_SameModelProviderState_Restored` — state re-keyed and stored. `FromProjection_RestoreProviderStateFalse_ClearsState` — state cleared on cross-model fork. 2 tests | ✅ |
| 4 | Session lineage | Already covered by `ModalityUsedEvent` (4.1) + `SessionForkRequest` (3.5). No new tests needed | ✅ |
| 5 | Replay/provider | `Projection_ToolCallTurn_ReconstructsCorrectly` — tool call + result + follow-up. `Projection_TokenUsage_AggregatesCorrectly` — input/output summed. `Projection_Reset_ClearsState` — messages/tokens/state cleared. `Projection_MultiToolBatch_ReconstructsCorrectly` — parallel tool calls. 4 tests | ✅ |

## Key Test Observations

- **Completeness audit** is the strongest guardrail: adding a new `OmicronEvent` subtype without updating the projector will fail the test, forcing the developer to either handle it or explicitly add it to the ignore list.
- **Provider state tests** verify both restore (`true`) and clear (`false`) paths, matching the fork/resume behavior in `OmicronHost`.
- **Multi-tool batch test** verifies parallel tool calls reconstruct into correct message ordering (Assistant w/ ToolCalls → ToolResult × N → Assistant).
- **Reset test** confirms messages, token usage, and provider state are all cleared after `SessionResetEvent`.

## Verdict

Clean hardening pass. No code changes needed beyond the comment addition. All 5 work items covered. The completeness audit test is the most valuable addition — it prevents silent drift between event types and projection.
