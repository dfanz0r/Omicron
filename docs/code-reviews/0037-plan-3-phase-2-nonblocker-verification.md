# Code Review 0037: Plan 3 Phase 2 Non-Blocker Verification

Date: 2026-05-08  
Scope: verify non-blocking follow-ups from `docs/code-reviews/0036-plan-3-phase-2-completion-review.md`.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 235

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

## Summary

One non-blocker was completed, while two remain as follow-up items.

## Verified Completed

### 1. Consecutive tool-use iteration regression test added

File:

- `Omicron.Core.Tests/SessionProjectionTests.cs`

Verified new test:

```csharp
ConsecutiveToolUseIterations_EachProduceSeparateAssistantMessages
```

This covers the important distinction between:

- one assistant tool-use turn with multiple tool calls; and
- consecutive assistant tool-use turns.

The test asserts that separate `AssistantResponseCompleteEvent` tool-turn boundaries produce separate assistant tool-call messages and that token usage accumulates across both tool turns plus the final response.

## Still Open / Not Completed

### 2. CLI UX for tool-call turn `AssistantResponseCompleteEvent` is still unchanged

File:

- `Omicron.CLI/Program.cs`

The CLI still prints token usage for every `AssistantResponseCompleteEvent`:

```csharp
case AssistantResponseCompleteEvent complete:
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(
        $"\n(Used {complete.InputTokens}↑ + {complete.OutputTokens}↓ tokens)");
    Console.ResetColor();
    break;
```

Since `AgentSession` now emits `AssistantResponseCompleteEvent` for tool-call turns, live CLI sessions may print token usage before tool execution as well as after the final response.

This may be acceptable, but it has not been explicitly handled or tested. Recommended follow-up:

- live-test a tool-call flow in CLI;
- decide whether to show per-provider-turn usage, suppress usage for tool-call turns, or aggregate/display per user turn.

This remains non-blocking for Phase 2 core replay completion.

### 3. Tool-call assistant timestamps are still approximate

File:

- `Omicron.Core/Sessions/SessionProjection.cs`

Projection now uses event-derived timestamps, which is much better than `DateTime.UtcNow`. However, assistant tool-call messages use the timestamp active when `FlushToolCallRun()` fires. That can be the timestamp of the next boundary event rather than the exact original `AssistantResponseCompleteEvent` that marked the tool-use turn.

This remains acceptable for MVP display/debug replay. If exact transcript timestamps become important, introduce explicit assistant-turn timestamp tracking or a dedicated assistant tool-call turn event.

## Recommendation

The completed regression test strengthens Phase 2. The remaining two items are legitimate non-blockers and should stay tracked for CLI/live testing and future exact replay fidelity.

Phase 2 remains complete for MVP core replay projection. Proceed to Plan 3 Phase 3: host-backed workspace VFS v1.
