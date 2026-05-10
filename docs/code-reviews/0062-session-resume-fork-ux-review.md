# Code Review 0062: Session Resume/Fork UX Review

Date: 2026-05-08  
Scope: review of current Plan `0003.5-session-resume-fork-ux.md` implementation state.

## Validation

```text
dotnet test Omicron.slnx --nologo
Passed: 339

dotnet build Omicron.slnx --nologo
0 warnings, 0 errors
```

Reviewed files:

```text
docs/implementation-plans/0003.5-session-resume-fork-ux.md
Omicron.Core/Sessions/AgentSession.cs
Omicron.Core/Sessions/SessionProjection.cs
Omicron.Core/OmicronHost.cs
Omicron.CLI/SlashCommandDispatcher.cs
Omicron.CLI/Program.cs
Omicron.Core.Tests/SessionResumeTests.cs
```

## Summary

The implementation has started and covers several intended pieces:

- `AgentSession.FromProjection(...)` exists.
- `OmicronHost.ResumeSessionAsync(...)` exists.
- `OmicronHost.ForkSessionAsync(...)` exists.
- CLI commands `/sessions`, `/resume`, and `/fork` exist.
- Basic resume/fork tests exist.

However, there are several correctness issues before this should be considered Plan 0003.5 complete. The most serious issues are:

1. CLI resume/fork currently appears to discard the returned session because of outer-loop control flow.
2. Forked sessions do not persist the replayed transcript into the new session event log, so later resume of the fork loses pre-fork context.
3. Provider state restoration likely does not work because projected provider-state keys use the original `AgentId`, while hydrated sessions create a new `AgentId`.

## Blocking Findings

### 1. CLI resume/fork result is discarded by `Program.cs` outer loop

File:

```text
Omicron.CLI/Program.cs
```

When `ChatLoop(...)` returns a new session/model, the outer loop handles it here:

```csharp
if (loopResult.NewSession is not null && loopResult.NewModel is not null)
{
    session = loopResult.NewSession;
    selectedModel = loopResult.NewModel;
    session.ApiKey = apiKey;
    ...
    slashContext = new SlashCommandContext(...);
    continue;
}
```

But `session` and `selectedModel` are local variables inside the `while (true)` iteration. `continue` jumps to the top of the outer loop, where `selectedModel` is recomputed from `currentModelKey` and a brand-new session is created with `host.CreateSession(...)`.

Impact:

- `/resume` and `/fork` may print success but then start a fresh new session instead of continuing the resumed/forked session.
- If fork target model differs, `currentModelKey` is not updated either.

Recommendation:

Refactor the loop so a resumed/forked session is actually used for the next chat loop. Options:

1. Maintain active session/model outside the outer model-selection loop.
2. Return a session-switch result and enter `ChatLoop(...)` again directly without creating a new session.
3. Update `currentModelKey` for the new model and avoid calling `host.CreateSession(...)` when `loopResult.NewSession` is supplied.

Add a CLI/controller-level test if possible, or factor the session-switch logic into a testable method.

---

### 2. Forked sessions do not persist replayed transcript events

File:

```text
Omicron.Core/OmicronHost.cs
```

`ForkSessionAsync(...)` creates a new `AgentSession` with hydrated in-memory messages and creates a new `SessionRecord`:

```csharp
var session = AgentSession.FromProjection(... existingSessionId: null ...);
await SessionStore.CreateSessionAsync(request, ct);
```

But it does not append events representing the replayed transcript to the new session's event log.

Impact:

- The fork works only in memory for the current process.
- If the user later resumes the forked session, `SessionProjector` will only see post-fork events and lose the copied pre-fork transcript.
- This violates the expected “fork copies transcript” persistence semantics.

Recommendation:

Choose and implement one durable fork model:

1. **Materialized fork**: append replay events to the new session log, e.g. synthetic `SessionStartedEvent`, `UserMessageEvent`, `AssistantResponseCompleteEvent`, tool result events where possible.
2. **Lineage fork**: persist source session id + fork point metadata and teach resume projection to project source events plus fork events.

For MVP, materialized fork is simpler if event fidelity is good enough. If not, add explicit fork metadata now and defer full branch projection, but do not present fork as durable transcript copy until implemented.

Add test:

```text
ForkSession_PersistedForkCanBeResumedWithCopiedTranscript
```

---

### 3. Provider state restore likely uses stale `AgentId` keys

Files:

```text
Omicron.Core/Sessions/AgentSession.cs
Omicron.Core/OmicronHost.cs
```

`AgentSession.FromProjection(...)` creates a new `AgentId` through the constructor. It then restores projected provider states as-is:

```csharp
foreach (var (key, state) in projection.ProviderStates)
    providerState.Set(state, reason: "session_hydration");
```

But `RunLoopAsync(...)` later looks up provider state using the hydrated session's new `AgentId`:

```csharp
var providerStateKey = ProviderStateKey.Create(
    Id, AgentId, Model.ProviderName, Model.Id, Model.ApiType);
```

The projected provider-state keys were emitted by the original session's original `AgentId`. Since the hydrated session has a different `AgentId`, lookup will miss the restored state.

Impact:

- same-model resume probably does not actually preserve provider continuation state;
- restored provider state may remain orphaned in the store under old agent id;
- tests do not currently verify lookup by hydrated session's actual key.

Recommendation:

Either:

1. Preserve/restore `AgentId` on same-session resume; or
2. Re-key provider state to the hydrated session's new `AgentId` before setting it; or
3. Explicitly do not restore provider state until agent identity semantics are handled.

Given same-session resume semantics, preserving the original `AgentId` may be best, but that requires projecting or storing it reliably.

Add test:

```text
ResumeSession_SameModel_RestoresProviderStateForHydratedAgentId
```

---

### 4. Fork system prompt fallback is incorrect

File:

```text
Omicron.Core/OmicronHost.cs
```

`ForkSessionAsync(...)` creates a session record with:

```csharp
SystemPrompt: systemPrompt ?? projection.Messages.FirstOrDefault()?.Text
```

The first projected message is usually the first user message, not the system prompt.

Impact:

- forked session record may store user text as system prompt;
- future resume/fork may use wrong prompt metadata.

Recommendation:

Load the source `SessionRecord` and use:

```csharp
SystemPrompt: systemPrompt ?? sourceRecord.SystemPrompt
```

If source record is missing, return null or create with explicit null/default.

---

### 5. `/persist` was reintroduced in help/completions but has no handler

File:

```text
Omicron.CLI/SlashCommandDispatcher.cs
```

`/persist` appears in `CommandNames` and help output:

```csharp
"/persist"
Console.WriteLine("    /persist            Show session storage type and event counts");
```

But there is no switch case for `persist`. Earlier we intentionally removed `/persist` as a toggle because disk persistence is default.

Impact:

- tab completion/help advertises a command that returns unknown command;
- contradicts previous decision that `/persist` should not exist as a persistence toggle.

Recommendation:

Either remove it again, or implement it as read-only `/status`-style diagnostics. Given earlier decision, prefer removing it from help/completions unless a read-only command is explicitly desired.

---

### 6. `/fork --model <n>` is documented but not implemented

Files:

```text
docs/implementation-plans/0003.5-session-resume-fork-ux.md
Omicron.CLI/SlashCommandDispatcher.cs
```

Plan says:

```text
/fork <session-id-prefix|index> [--model <model-key|n>]
```

Implementation only treats `--model` as a catalog key:

```csharp
if (context.Catalog.Models.TryGetValue(modelArg, out var m)) ...
else Console.WriteLine($"Model '{modelArg}' not found.");
```

Impact:

- `/fork 1 --model 2` does not work despite the plan.

Recommendation:

Reuse the `/model <n|key>` visible-model resolution helper so `/fork --model` supports both visible index and key.

## Medium-Priority Findings

### 7. Prefix matching is ambiguous

Files:

```text
Omicron.CLI/SlashCommandDispatcher.cs
```

`/resume` and `/fork` use first prefix match:

```csharp
sessions.FirstOrDefault(s => s.SessionId.ToString().StartsWith(args, ...))
```

If multiple sessions share a prefix, the command should report ambiguity rather than silently selecting the first.

Tracked as future hardening item `FH-0010`.

### 8. Slash command handlers block on async session store calls

Several CLI handlers call async APIs via `.GetAwaiter().GetResult()`. This is acceptable in the current synchronous slash dispatcher, but it will not scale well if the CLI becomes async or if stores become slower/remote.

Tracked as future hardening item `FH-0011`.

### 9. Fork lineage metadata is not represented

Even if MVP uses materialized fork events, future debugging/UI will need source-session lineage.

Tracked as future hardening item `FH-0012`.

## Test Gaps

Existing tests cover basic resume/fork in memory, but should be strengthened:

- resumed CLI/session switch actually continues with resumed session;
- forked session can be persisted, reopened, and resumed with copied transcript;
- same-model resume provider state is available under hydrated session key;
- fork record preserves source system prompt;
- cross-model fork clears provider state and does not preserve old provider-state keys;
- `/fork --model <n>` works or docs are corrected;
- `/persist` is not advertised unless implemented.

## Recommendation

Do not mark Plan 0003.5 complete yet.

Fix blockers first:

1. Correct CLI session-switch control flow.
2. Make fork transcript durable or explicitly change semantics.
3. Fix provider-state restoration key/agent-id behavior.
4. Fix fork system prompt source.
5. Remove or implement `/persist`.
6. Implement `/fork --model <n>` or update the plan.

Then rerun full validation and add a final completion review.
