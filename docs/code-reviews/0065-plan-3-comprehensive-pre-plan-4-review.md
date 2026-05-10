# Code Review 0065 — Plan 3 Comprehensive Pre-Plan-4 Review

**Scope:** Full codebase audit before entering Plan 4 (transaction-backed edit harness).  
**Date:** 2026-05-08  
**Test Status:** 346 passed, 0 warnings, 0 errors  
**Review Type:** Deep architectural + tactical review — blockers, warts, missing features, refactors, performance, and testing gaps.

---

## 1. Architectural Warts

### 1.1 `Message` is a mutable `class` with `init`-only properties
**Location:** `Omicron.Core/Models/Message.cs`  
**Severity:** Medium  
**Issue:** `Message` uses `class` semantics but with `init`-only properties, giving it a record-like API without value semantics. This creates confusion — it looks immutable but reference-equality is used. It also means `AgentSession._messages` stores reference copies that can accidentally share mutable sub-objects (`ToolCalls` list, `Images` list).  
**Fix:** Convert `Message` to a `sealed record` (or `sealed record class` in C# 12). The factory methods (`UserMessage`, `AssistantMessage`, etc.) already work with records.  
**Plan 4 Impact:** Plan 4 may snapshot messages for edit history; value semantics make this safer.

### 1.2 `AgentSession` has post-construction mutable configuration
**Location:** `Omicron.Core/Sessions/AgentSession.cs` (lines 45–55)  
**Severity:** Medium  
**Issue:** `Model`, `SystemPrompt`, `ApiKey`, `MaxTokens`, `Temperature`, `ReasoningEffort`, `MaxIterations` are all settable after construction. This creates temporal coupling — `CreateSession` sets some, then the CLI mutates others. A resumed/forked session may have config drift between the `SessionRecord` and the runtime object.  
**Fix:** Make these `init`-only or move into a `SessionConfig` record passed at construction.  
**Plan 4 Impact:** Transaction-backed edit harness needs deterministic session state snapshots.

### 1.3 `Model` holds runtime `IChatProvider` reference
**Location:** `Omicron.Core/Models/Model.cs` (line 44)  
**Severity:** Low-Medium  
**Issue:** `Model` is supposed to be catalog metadata, but it carries a mutable `Provider` property. Both `ProviderFactory` and `ModelCatalogService` mutate this. This creates dual ownership — the catalog thinks it owns the model, but the provider registry also injects itself.  
**Fix:** Resolve providers at call time via `IProviderRegistry.GetProvider(model.ProviderName)` rather than caching on `Model`.  
**Plan 4 Impact:** Low direct impact, but cleaner separation helps when models move between providers in forks.

### 1.4 `PersistentEventSink` uses sync-over-async
**Location:** `Omicron.Core/Events/PersistentEventSink.cs` (lines 67, 95)  
**Severity:** Medium  
**Issue:** `Emit()` and `EmitBatch()` call `_store.AppendEventsAsync(...).GetAwaiter().GetResult()` inside non-async methods. This can deadlock in contexts with a synchronization context (e.g., UI thread, ASP.NET).  
**Mitigation:** The CLI has no sync context, so it works today.  
**Fix:** Add `EmitAsync`/`EmitBatchAsync` overloads to `IEventSink` for async callers. Keep sync variants for convenience but document the sync-over-async hazard.  
**Plan 4 Impact:** Plan 4 may introduce async command handlers or background workers that call into the event sink.

### 1.5 `InMemoryEventSink` uses `Interlocked.Increment` inside a `lock`
**Location:** `Omicron.Core/Events/IEventSink.cs` (lines 56, 74)  
**Severity:** Low  
**Issue:** `Interlocked.Increment(ref _globalSequence)` is redundant when immediately followed by a `lock` block. The lock already guarantees atomicity. The interlocked adds overhead and implies cross-thread semantics that aren't needed because the lock serializes access anyway.  
**Fix:** Replace `Interlocked.Increment` with `++_globalSequence` inside the lock.  
**Plan 4 Impact:** None, but cleaner code.

### 1.6 `AgentSession.Reset()` does not yield from async stream
**Location:** `Omicron.Core/Sessions/AgentSession.cs` (line 118)  
**Severity:** Low (documented behavior)  
**Issue:** `Reset()` is synchronous and emits `SessionResetEvent` only to the sink. Callers iterating `PromptAsync` won't see the reset event in their stream. This is documented but remains a footgun for UI authors.  
**Fix:** Consider returning the event or documenting even more prominently.  
**Plan 4 Impact:** UI layers building on Plan 4 need to know reset happened.

### 1.7 `ShapeBasedProvider` endpoint routing incomplete for `GoogleGenAi`
**Location:** `Omicron.Core/Providers/IChatProvider.cs` (ShapeBasedProvider.StreamAsync, ~line 306)  
**Severity:** Medium  
**Issue:** The endpoint switch defaults to `/chat/completions` for unknown `ApiType` values. `GoogleGenAi` is defined in the enum but has no `IApiShape` implementation and would be routed to the OpenAI chat endpoint incorrectly.  
**Fix:** Either remove `GoogleGenAi` from the enum until implemented, or add a placeholder shape that throws `NotSupportedException`.  
**Plan 4 Impact:** Low unless Google models are added to the catalog.

### 1.8 `Conversation` canonical model exists but is unused at runtime
**Location:** `Omicron.Core/Models/Conversation.cs`  
**Severity:** Low  
**Issue:** `ConversationTurn`, `ConversationContent`, and `ConversationConverter` exist but `AgentSession` and all providers use `Message` directly. The canonical model is only used in tests.  
**Fix:** Either adopt it in `AgentSession` (big refactor) or document it as a future target and reduce its surface area.  
**Plan 4 Impact:** Plan 4 may need richer content models (edits as content blocks); the canonical format should be the target.

---

## 2. Missing Features (Should Exist Per Prior Plans)

### 2.1 No `GoogleGenAi` ApiShape implementation
**Location:** `Omicron.Core/Providers/ApiShape.cs`  
**Severity:** Medium  
**Issue:** `ApiType.GoogleGenAi` exists in the enum and is referenced in `CompatibilityDetector`, but no `IApiShape` implementation exists. `OpenCodeProvider` registers it in its `_shapes` dictionary but the value is missing — it would crash at runtime if a Google model were selected.  
**Fix:** Remove from enum until implemented, or add a stub shape.  
**Plan 4 Impact:** Plan 4 may not need Google directly, but incomplete enum values are a runtime hazard.

### 2.2 No transaction audit events
**Location:** `Omicron.Core/Workspace/WorkspaceTransaction.cs`  
**Severity:** Low (deferred per FH-0007)  
**Issue:** `WorkspaceTransaction` does not emit `TransactionStartedEvent`, `TransactionCommittedEvent`, `TransactionRolledBackEvent`, etc. This was deferred because "session/tool ownership was unclear."  
**Fix:** Add events scoped to the transaction ID. The session ID is available via `AgentSession.Id` if the transaction is created within session context.  
**Plan 4 Impact:** Plan 4's edit harness needs to observe transaction lifecycle for undo/redo and audit logs.

### 2.3 No `IWorkspaceTransaction` event emission
**Location:** `Omicron.Core/Workspace/WorkspaceTransaction.cs`  
**Severity:** Medium  
**Issue:** The transaction interface has no `IEventSink` dependency. A caller staging writes has no way to observe what happened without calling `GetDiffAsync()` after the fact.  
**Fix:** Add optional `IEventSink` to `WorkspaceTransaction` constructor, emit events on stage/commit/rollback.  
**Plan 4 Impact:** The edit harness needs real-time observation of staged changes.

### 2.4 `AgentSession` does not expose a `ContinueAsync` with tool-call test
**Location:** `Omicron.Core.Tests/AgentSessionTests.cs`  
**Severity:** Low (testing gap, but also a feature gap)  
**Issue:** `ContinueAsync` is tested for error cases (no conversation, after reset) but not for the happy path continuing after a tool-call turn.  
**Fix:** Add a test that calls `PromptAsync` with a tool-use response, then `ContinueAsync` to verify the loop resumes correctly.  
**Plan 4 Impact:** Plan 4 may use `ContinueAsync` for applying edits without new user input.

### 2.5 `WorkspaceTransactionManager` has no host-agnostic interface
**Location:** `Omicron.Core/Workspace/WorkspaceTransactionManager.cs`  
**Severity:** Low  
**Issue:** `IWorkspaceTransactionManager.BeginTransaction()` takes no parameters. There's no way to begin a transaction with a custom event sink, a snapshot base, or a different root.  
**Fix:** Add overloads or a `BeginTransactionOptions` parameter.  
**Plan 4 Impact:** Plan 4 may need transactions over non-host VFS (e.g., remote workspaces, Git blobs).

---

## 3. Small Refactors (Direct Code Review Findings)

### 3.1 `JsonlSessionStore.SerializeEvent` allocates per call
**Location:** `Omicron.Core/Sessions/SessionStore.cs` (line 362)  
**Severity:** Low  
**Finding:** `SerializeEvent` creates a new `JsonSerializerOptions` instance for every event. In a high-volume session this is unnecessary GC pressure.  
**Fix:** Use the instance `_jsonOptions` field or cache a static `JsonSerializerOptions` for serialization.  
**Backlog:** No — this is a trivial fix that should be done immediately.

### 3.2 `TextLineSplitter` double-normalizes `\r`
**Location:** `Omicron.Core/Diff/TextLineSplitter.cs` (line 14)  
**Severity:** Trivial  
**Finding:** `.Replace("\r\n", "\n").Replace("\r", "\n")` — after the first replace, standalone `\r` characters are already gone (they were part of `\r\n`). The second replace is redundant.  
**Fix:** Remove the second `.Replace("\r", "\n")`.  
**Backlog:** No — direct fix.

### 3.3 `DisplayHelpers` lives in `Program.cs`
**Location:** `Omicron.CLI/Program.cs`  
**Severity:** Trivial  
**Finding:** `DisplayHelpers` is a public static class in the CLI entry point. It should be in its own file or in a `Utils` folder.  
**Fix:** Move to `Omicron.CLI/DisplayHelpers.cs`.  
**Backlog:** No — direct fix.

### 3.4 `AgentSession.RunLoopAsync` is ~250 lines
**Location:** `Omicron.Core/Sessions/AgentSession.cs` (lines 195–350)  
**Severity:** Low  
**Finding:** The core agent loop handles streaming, provider state, tool calls, permission checks, and error handling all in one method. It's hard to follow and test in isolation.  
**Fix:** Extract private methods: `BuildChatOptions()`, `StreamResponseAsync()`, `HandleToolCallsAsync()`, `BuildAssistantMessage()`.  
**Backlog:** Add to `FUTURE-HARDENING-BACKLOG.md` as FH-0013.

### 3.5 `WorkspaceReadService` and `LocalExecutionBroker` duplicate constants
**Location:** `Omicron.Core/Workspace/WorkspaceReadService.cs` and `Omicron.Core/Execution/IExecutionBroker.cs`  
**Severity:** Trivial  
**Finding:** Both define `MaxOutputLines = 2000` and `MaxOutputBytes = 50 * 1024`.  
**Fix:** Move to a shared `OmicronConstants` class.  
**Backlog:** No — direct fix.

### 3.6 `ToolSchema` builds schemas imperatively
**Location:** `Omicron.Core/Tools/Tool.cs`  
**Severity:** Low  
**Finding:** `ToolSchema.Object()` uses `Utf8JsonWriter` to build JSON from scratch every time. Tool schemas are typically static per tool definition.  
**Fix:** Cache the `JsonElement` results or accept that the imperative builder is fine for MVP but document it.  
**Backlog:** Add to backlog as FH-0014.

### 3.7 `WorkspaceTransaction` path normalization is duplicated
**Location:** `Omicron.Core/Workspace/WorkspaceTransaction.cs` (line 45)  
**Severity:** Trivial  
**Finding:** `NormalizePath` is called in every staging method. `WorkspacePath` could encapsulate this.  
**Fix:** Add a `NormalizedValue` property to `WorkspacePath` or use it consistently.  
**Backlog:** No — direct fix.

---

## 4. Performance Issues

### 4.1 `JsonlSessionStore` opens a new file stream per batch
**Location:** `Omicron.Core/Sessions/SessionStore.cs` (line 278)  
**Severity:** Medium  
**Issue:** `AppendEventsAsync` opens a new `FileStream` for every call. In a busy session with many events, this causes repeated file open/close syscalls.  
**Fix:** Consider keeping a per-session `StreamWriter` open during active sessions, or batch more aggressively.  
**Plan 4 Impact:** Plan 4 may generate many small events (edit staging, file reads); file I/O could become a bottleneck.

### 4.2 `HostWorkspaceFileSystem.ReadDirectoryAsync` blocks thread for line counts
**Location:** `Omicron.Core/Workspace/WorkspaceVfs.cs` (line 179)  
**Severity:** Medium  
**Issue:** `File.ReadLines(entry).Count()` is synchronous I/O inside an async method. For directories with many text files, this blocks the thread pool.  
**Fix:** Use async line counting or make line count lazy (compute on demand).  
**Plan 4 Impact:** Plan 4 may scan large directories; blocking I/O hurts responsiveness.

### 4.3 `WorkspaceReadService.ParseLines` has multiple encoding passes
**Location:** `Omicron.Core/Workspace/WorkspaceReadService.cs` (line 98)  
**Severity:** Low  
**Issue:** Bytes → string (`UTF8.GetString`), split lines, then per-line `UTF8.GetByteCount` for truncation checking. Three passes over the data.  
**Fix:** Count bytes while splitting, or use a streaming line reader.  
**Plan 4 Impact:** Large files (>1MB) will see noticeable overhead.

### 4.4 `TraceMyersDiffStrategy` allocates per D-level
**Location:** `Omicron.Core/Diff/TraceMyersDiffStrategy.cs` (line 22)  
**Severity:** Low  
**Issue:** `var snapshot = new int[V.Length]; Array.Copy(V, snapshot, V.Length);` for every D level. For large diffs this is O(D²) memory.  
**Fix:** This is inherent to the trace algorithm; the divide-and-conquer strategy already exists as the fallback. Ensure the threshold (2000 lines) is appropriate.  
**Plan 4 Impact:** Diffing large files in transactions could hit memory pressure.

### 4.5 `LineNormalizer.GetCode` allocates `StringBuilder` per line
**Location:** `Omicron.Core/Diff/TextDiffEngine.cs` (line 96)  
**Severity:** Low  
**Issue:** When `IgnoreWhitespaceRuns` is true, a new `StringBuilder` is created for every line.  
**Fix:** Use `StringBuilderCache` (internal .NET pattern) or pool `StringBuilder` instances.  
**Plan 4 Impact:** Diffing large files with whitespace normalization enabled.

### 4.6 `SessionProjector` uses unbounded `StringBuilder`
**Location:** `Omicron.Core/Sessions/SessionProjection.cs` (lines 62–63)  
**Severity:** Low  
**Issue:** `accumText` and `accumReasoning` append indefinitely across events. A very long assistant response (or many deltas) could cause unbounded growth.  
**Fix:** This is correct for projection (the text is needed), but consider capping or using a chunked approach for extremely long sessions.  
**Plan 4 Impact:** Long-running sessions with large outputs.

---

## 5. Testing Gaps

### 5.1 No direct tests for `DivideAndConquerMyersDiffStrategy`
**Location:** `Omicron.Core.Tests/TextDiffEngineTests.cs`  
**Severity:** Medium  
**Finding:** All diff tests go through `TextDiffEngine.DiffLines()`, which may or may not hit the divide-and-conquer fallback. There are no unit tests directly exercising the D&C strategy's edge cases (empty inputs, all-same, all-different, single-element arrays).  
**Fix:** Add `DivideAndConquerMyersDiffStrategyTests` with direct invocation.

### 5.2 No tests for `AnthropicProvider` streaming
**Location:** `Omicron.Core.Tests/ProviderTests.cs`  
**Severity:** Medium  
**Finding:** `AnthropicProvider` has its own `StreamAsync` implementation (not inherited from `ShapeBasedProvider`). There are no tests for its SSE parsing, tool call accumulation, or error handling.  
**Fix:** Add `AnthropicProviderTests` with a fake HTTP handler.

### 5.3 No tests for `LineEditor`
**Location:** `Omicron.CLI/LineEditor.cs`  
**Severity:** Medium  
**Finding:** `LineEditor` has zero test coverage. It handles complex logic (paste detection, tab completion, multiline input, escape handling). Bugs here directly affect UX.  
**Fix:** Add `LineEditorTests` using a mocked console input stream or by extracting the logic into a testable state machine.

### 5.4 No tests for `JsonlSessionStore` concurrent access
**Location:** `Omicron.Core.Tests/JsonlSessionStoreTests.cs`  
**Severity:** Medium  
**Finding:** The store uses `lock` internally, but no test validates concurrent `AppendEventsAsync` from multiple threads.  
**Fix:** Add a concurrent stress test.

### 5.5 No tests for `WorkspaceTransaction` commit failure recovery
**Location:** `Omicron.Core.Tests/WorkspaceTransactionTests.cs`  
**Severity:** Medium  
**Finding:** `CommitAsync` has a try/catch that sets `_committed = false` on failure, but no test verifies this behavior or the partial-commit scenario.  
**Fix:** Add a test using a mock `HostWorkspaceFileSystem` that throws mid-commit.

### 5.6 No tests for `OpenCodeProvider` routing logic
**Location:** `Omicron.Core/Providers/OpenCodeProvider.cs`  
**Severity:** Low  
**Finding:** `ResolveApiType`, `ResolveBaseUrl`, and the `ResponsesModelIds` set are not directly tested.  
**Fix:** Add `OpenCodeProviderTests`.

### 5.7 No tests for `ProviderStateManager.ClearSession`
**Location:** `Omicron.Core.Tests/ProviderStateTests.cs`  
**Severity:** Low  
**Finding:** `ClearSession` is tested on the raw store but not on `ProviderStateManager` (which emits events).  
**Fix:** Add a test verifying that `ClearSession` emits the correct number of `ProviderStateClearedEvent`s.

### 5.8 No tests for `ConfigManager` save/load round-trip
**Location:** `Omicron.Core/Config/AgentConfig.cs`  
**Severity:** Low  
**Finding:** Config persistence is untested. A regression in Tomlyn serialization would break user configs.  
**Fix:** Add `ConfigManagerTests` in a temp directory.

### 5.9 No tests for `CompatibilityDetector` with Google provider
**Location:** `Omicron.Core.Tests/CompatibilityDetectorTests.cs`  
**Severity:** Low  
**Finding:** Tests cover OpenAI, Anthropic, OpenCode, OpenRouter, and unknown providers, but not `google` or `google-genai`.  
**Fix:** Add inline data for Google.

### 5.10 No tests for `WorkspaceTransaction` overlay `ReadDirectoryAsync` with nested directories
**Location:** `Omicron.Core.Tests/WorkspaceTransactionTests.cs`  
**Severity:** Low  
**Finding:** `ReadDirectoryAsync` is tested for staged writes/deletes at the root level but not for nested directory listings (e.g., staging a write at `a/b/c.txt` and reading `a/b`).  
**Fix:** Add a nested directory listing test.

---

## 6. Plan 4 Blockers

The following issues will make Plan 4 (transaction-backed edit harness) more difficult if left unaddressed.

| # | Issue | Location | Urgency |
|---|-------|----------|---------|
| B-1 | `WorkspaceTransaction.CommitAsync` is non-atomic | `WorkspaceTransaction.cs:195` | **High** |
| B-2 | `Message` mutability makes snapshotting unsafe | `Message.cs` | Medium |
| B-3 | `AgentSession` config mutability prevents deterministic replay | `AgentSession.cs:45-55` | Medium |
| B-4 | No transaction lifecycle events for observation | `WorkspaceTransaction.cs` | **High** |
| B-5 | `PersistentEventSink` sync-over-async risk | `PersistentEventSink.cs:67` | Medium |
| B-6 | `LineEditor` sync-only API blocks async commands | `LineEditor.cs` | Low |
| B-7 | `IWorkspaceTransactionManager` is host-locked | `WorkspaceTransactionManager.cs` | Medium |
| B-8 | No `IWorkspaceTransaction` rollback-on-failure | `WorkspaceTransaction.cs:195` | **High** |

### B-1 / B-8: Non-atomic commit and no rollback-on-failure
**Details:** `CommitAsync` applies staged changes one by one. If a write succeeds and a subsequent delete throws, the transaction is left in a partially-committed state with `_committed = false`. The caller has no way to know what succeeded.  
**Plan 4 Requirement:** Edit harness must either commit atomically or provide a recovery path.  
**Fix Options:**
1. Build a two-phase commit: write all files to temp names, then rename them all at the end.
2. Track which operations succeeded and auto-rollback on failure.
3. Document that MVP transactions are best-effort and require manual cleanup.

### B-4: No transaction lifecycle events
**Details:** The edit harness needs to observe when a transaction starts, stages changes, commits, or rolls back. Without events, the UI can't show a live diff or enable undo.  
**Fix:** Add `TransactionStartedEvent`, `TransactionStagedEvent`, `TransactionCommittedEvent`, `TransactionRolledBackEvent` to the event model, and emit them from `WorkspaceTransaction`.

### B-2 / B-3: Mutable state prevents reliable snapshots
**Details:** Plan 4 may snapshot the session + workspace state before applying an edit. If `Message`, `AgentSession.Model`, etc. are mutable, shallow copies share references.  
**Fix:** Convert `Message` to a record. Freeze `AgentSession` config after construction (or snapshot into a `SessionState` record).

---

## 7. Immediate Action Items (Non-Backlog)

These are small, safe fixes that should be done before starting Plan 4.

1. **Convert `Message` to `sealed record`** — `Omicron.Core/Models/Message.cs`
2. **Remove redundant `Interlocked.Increment` in `InMemoryEventSink`** — `Omicron.Core/Events/IEventSink.cs`
3. **Cache `JsonSerializerOptions` in `JsonlSessionStore.SerializeEvent`** — `Omicron.Core/Sessions/SessionStore.cs`
4. **Fix `TextLineSplitter` double `\r` replace** — `Omicron.Core/Diff/TextLineSplitter.cs`
5. **Move `DisplayHelpers` out of `Program.cs`** — `Omicron.CLI/Program.cs`
6. **Deduplicate `MaxOutputLines`/`MaxOutputBytes` constants** — `WorkspaceReadService.cs`, `LocalExecutionBroker.cs`
7. **Add `AnthropicProvider` streaming tests** — `Omicron.Core.Tests/`
8. **Add `DivideAndConquerMyersDiffStrategy` direct tests** — `Omicron.Core.Tests/`
9. **Add `WorkspaceTransaction` nested directory overlay test** — `Omicron.Core.Tests/WorkspaceTransactionTests.cs`
10. **Add `GoogleGenAi` stub or remove from enum** — `Omicron.Core/Providers/ApiShape.cs`

---

## 8. Backlog Items Added

| ID | Item | File |
|----|------|------|
| FH-0013 | Refactor `AgentSession.RunLoopAsync` into smaller private methods | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0014 | Cache `ToolSchema` outputs or document imperative builder | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0015 | Add `EmitAsync`/`EmitBatchAsync` to `IEventSink` to eliminate sync-over-async | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0016 | Adopt `ConversationTurn` canonical model in `AgentSession` (long-term) | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0017 | Make `AgentSession` config immutable after construction | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0018 | Add transaction lifecycle events (`TransactionStartedEvent`, etc.) | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0019 | Make `WorkspaceTransaction.CommitAsync` atomic or add rollback tracking | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0020 | Decouple `Model.Provider` from catalog metadata | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0021 | Add `LineEditor` tests | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0022 | Add `JsonlSessionStore` concurrent access stress test | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0023 | Add `WorkspaceTransaction` commit failure recovery test | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0024 | Add `ConfigManager` save/load round-trip test | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0025 | Add `OpenCodeProvider` routing tests | `FUTURE-HARDENING-BACKLOG.md` |
| FH-0026 | Add `ProviderStateManager.ClearSession` event emission test | `FUTURE-HARDENING-BACKLOG.md` |

---

## 9. Verdict

**Ready for Plan 4 with caveats.**

The codebase is structurally sound for entering Plan 4. The transaction overlay, diff engine, and session replay are well-implemented and tested. However, **three issues should be addressed before significant Plan 4 work:**

1. **B-1/B-8 (non-atomic commit)** — The edit harness cannot reliably apply multi-file edits without atomicity or rollback tracking. This is the highest-priority blocker.
2. **B-4 (transaction events)** — The harness needs to observe transaction state for UI feedback and undo.
3. **Message mutability** — Converting `Message` to a record is a small change that prevents an entire class of snapshot bugs.

The remaining items (performance optimizations, additional tests, small refactors) can be done incrementally during Plan 4 or deferred to the hardening backlog.

---

*Review completed. 346 tests passing. Build clean.*
