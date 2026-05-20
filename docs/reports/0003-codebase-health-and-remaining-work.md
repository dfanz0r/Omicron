# Codebase Health & Remaining Work Report

**Date:** 2026-05-20  
**Author:** Automated analysis, validated against current source  
**Solution build:** 0 warnings, 0 errors (`dotnet build Omicron.slnx --no-restore`)  
**Solution tests:** 996 passing (`dotnet test Omicron.slnx --no-restore`)  
**Generator tests:** 2 passing (`dotnet test Omicron.Core.Generators.Tests/Omicron.Core.Generators.Tests.csproj --no-restore`)

---

## Executive Summary

The main Omicron solution is healthy: the solution build is clean and the solution test suite has 996 passing tests. The out-of-solution generator test project has also been fixed and now builds/tests cleanly with 2 passing tests. Several items previously listed as "unstarted" are now substantially or fully implemented, including model metadata, binary/adaptive file reading, semantic content blocks, provider/session replay hardening, and the TUI rendering/session path.

The main remaining product cleanup is the dual-state text bridge in `ToolResult` and `ContentProcessorResult`. Conversation messages and event payloads have moved to `Utf8String`, but tool/content processor results still carry `string` plus optional UTF-8 bytes.

---

## 1. Validation Performed

Commands run during this review:

```bash
dotnet build Omicron.slnx --no-restore
dotnet test Omicron.slnx --no-restore
dotnet build Omicron.Core.Generators.Tests/Omicron.Core.Generators.Tests.csproj --no-restore
dotnet test Omicron.Core.Generators.Tests/Omicron.Core.Generators.Tests.csproj --no-restore
```

Results:

| Scope | Result |
|-------|--------|
| `Omicron.slnx` build | ✅ 0 warnings, 0 errors |
| `Omicron.slnx` tests | ✅ 996 passed, 0 failed, 0 skipped |
| `Omicron.Core.Generators.Tests` build | ✅ 0 warnings, 0 errors |
| `Omicron.Core.Generators.Tests` tests | ✅ 2 passed, 0 failed, 0 skipped |

`Omicron.slnx` includes `Omicron.CLI`, `Omicron.Core`, and `Omicron.Core.Tests`; `Omicron.Core.Generators` is built as an analyzer project reference. `Omicron.Core.Generators.Tests` is a separate project and is not included in `Omicron.slnx`, so it should be run explicitly or added to the solution/CI if it is expected to be part of the default validation set.

---

## 2. Completed / Validated Work

### 2.1 UTF-8 Pipeline (Plans 0017–0021) — Complete in the solution

| Component | Status | Key Files |
|-----------|--------|-----------|
| `Utf8String` type | ✅ Implemented with `ManagedArray`, `StaticLiteral`, `Empty` storage kinds | `Omicron.Core/Content/Utf8String.cs` |
| `Utf8Builder` | ✅ Pooled `byte[]`-backed mutable builder, `IBufferWriter<byte>` | `Omicron.Core/Text/Utf8Builder.cs` |
| `Utf8Text.CreateBuilder()` factory | ✅ Implemented | `Omicron.Core/Text/Utf8Text.cs` |
| ZString dependency | ✅ Removed from product/test code and package references | `Omicron.Core/Omicron.Core.csproj` |
| `Utf8CompositeFormat` / prepared formats | ✅ Implemented and tested | `Omicron.Core/Text/Utf8CompositeFormat*.cs` |
| `Utf8ValueFormatter` | ✅ Centralized formatting, generated hook, format-specifier support | `Omicron.Core/Text/Utf8ValueFormatter.cs` |
| `Utf8Buffer` | ✅ Implemented and tested | `Omicron.Core/Text/Utf8Buffer.cs` |
| `Message` | ✅ UTF-8-native `TextData` / `ReasoningData` | `Omicron.Core/Models/Message.cs` |
| Provider request serialization | ✅ `IApiShape.WriteRequestBody(Utf8JsonWriter)` | `Omicron.Core/Providers/ApiShape.cs` |
| Provider SSE ingest | ✅ Byte-oriented reusable buffer with payload-copy safety | `Omicron.Core/Providers/IChatProvider.cs` |
| Conversation text event payloads | ✅ `Utf8String` for user, assistant, and tool-result text events | `Omicron.Core/Events/OmicronEvent.cs` |
| `Conversation` model | ✅ UTF-8-native text/tool-result content items | `Omicron.Core/Models/Conversation.cs` |
| `SessionProjection` | ✅ Uses `Utf8TextAccumulator` + `Utf8String.FromBuffer()` | `Omicron.Core/Sessions/SessionProjection.cs` |

### 2.2 Hardening Backlog (Plan 0003.6) — Medium/high items complete

The medium/high Plan 0003.6 items are implemented and covered by tests. The low-priority backlog remains tracked separately.

Notable completed items include JSONL concurrency, async event sink APIs, event registry, immutable `SessionConfig`, transaction lifecycle events, rollback tracking, line editor tests, ambiguous session prefix detection, and commit-failure recovery tests.

### 2.3 Other completed or substantially implemented plans

| Plan | Current state | Evidence |
|------|---------------|----------|
| 0003.1 Native diff engine | ✅ Implemented | `Omicron.Core/Diff/*`, `TextDiffEngineTests.cs` |
| 0003.25 Workspace transaction manager | ✅ Implemented | `IWorkspaceTransactionManager`, host wiring, transaction tests |
| 0003.7 API shape / async / model metadata | ✅ Implemented despite stale plan status | `CreateSessionAsync`, `SessionResumeRequest`, `ModelMetadata*`, tests |
| 0003.8 models.dev metadata / CLI context | ✅ Complete | Plan status says complete; `ModelMetadataTests.cs` |
| 0004 Transaction-backed edit harness | ✅ Implemented | `edit_file_hashline`, `read_file_hashlines`, `TransactionEditHarnessTests.cs` |
| 0004.1 Adaptive binary file reading / hex viewer | ✅ Implemented | `ContentProcessorRegistry`, `HexDumpProcessor`, `BinaryFileReaderTests.cs` |
| 0005 Semantic content blocks | ✅ Content block portion implemented; command primitive portion not evident | `IContentBlock`, block types, renderer, JSON converter, tests |
| 0006 Provider/session replay hardening | ✅ Largely implemented; lineage metadata still open | `ProviderStateTests.cs`, `SessionReplayHardeningTests.cs` |
| 0007 Terminal backend/foundation | ✅ Implemented | `Omicron.Core/Rendering/*`, `TerminalRenderingTests.cs` |
| 0007.1 Transcript viewport/app layout | ✅ Implemented | `TranscriptStore`, `TranscriptLayoutCache`, `TranscriptViewportTests.cs` |
| 0007.2 TUI session integration | ✅ Substantially implemented | `RunTuiSessionAsync`, `AppLayout`, `TuiSessionIntegrationTests.cs` |
| 0009 Kitty keyboard protocol | ✅ Implemented | `TerminalInputParser`, `KittyKeyboardProtocolTests.cs` |
| 0014 OS-specific terminal backends | ✅ Implemented | Linux/macOS/Windows terminal backends |
| 0020 Phase 6 formatter generator | ✅ Implemented in main solution tests | `Utf8FormatterGenerator`, `Utf8FormatterGeneratorTests.cs` |

---

## 3. Remaining Work

### 3.1 High / Medium Priority

#### 3.1.1 Include generator tests in default validation if desired

`Omicron.Core.Generators.Tests` now builds and passes, but it remains outside `Omicron.slnx`. If this project is intended to be a permanent regression suite, add it to the solution and CI validation set; otherwise keep documenting that it must be run explicitly.

#### 3.1.2 Migrate remaining dual-state tool/content result types

`ToolResult` is not the only remaining dual-state text bridge. `ContentProcessorResult` has the same pattern.

| Type | Current shape | File |
|------|---------------|------|
| `ToolResult` | `string? Text`, `ReadOnlyMemory<byte>? Utf8Data`, `List<IContentBlock>? Blocks` | `Omicron.Core/Tools/ToolRegistry.cs` |
| `ContentProcessorResult` | `string Text`, `ReadOnlyMemory<byte>? Utf8Data` | `Omicron.Core/IO/ContentProcessorRegistry.cs` |

Target direction:

```csharp
public sealed record ToolResult(
    Utf8String? TextData = null,
    bool IsError = false,
    List<IContentBlock>? Blocks = null);

public sealed record ContentProcessorResult(
    Utf8String TextData,
    OutputModality ActualModality,
    string? MimeType = null,
    string? Warning = null,
    bool IsTruncated = false,
    long? NextOffset = null);
```

Impacted areas include `AgentSession`, `BuiltinWorkspaceToolsExtension`, `BuiltinExecutionToolsExtension`, content processors, and tests that call `ToolResult.GetText()`.

#### 3.1.3 Tool call validation feedback (FH-0029)

Still open and medium priority. Malformed model tool calls can produce technically correct but model-unfriendly errors. Add a validation layer that returns structured repair hints before invoking tool implementations.

#### 3.1.4 Custom model sources and Azure API versions (FH-0028 / FH-0027)

Still open and useful for real users:

- Config-defined custom models/endpoints.
- Per-model/provider API version/query parameters, especially for Azure OpenAI Chat Completions.

### 3.2 Low Priority Hardening Backlog Still Open

The active low-priority backlog remains mostly accurate, except FH-0026 has been implemented.

| ID | Status | Notes |
|----|--------|-------|
| FH-0006 | Open | Workspace read continuation hints are still text-only/ambiguous. |
| FH-0008 | Open | `OmicronHost` still casts `FileSystem` to `HostWorkspaceFileSystem` for transaction manager wiring. |
| FH-0009 | Open | `OmicronHost_TransactionManager_CreatesWorkingTransaction` still lives in `PersistenceIntegrationTests.cs`. |
| FH-0011 | Open | `SlashCommandDispatcher` still contains sync-over-async store calls. |
| FH-0012 | Open | Fork lineage metadata/events are still not represented. |
| FH-0013 | Open | `AgentSession.RunLoopAsync` remains large and multi-responsibility. |
| FH-0014 | Open | `ToolSchema` builders still build JSON per call. |
| FH-0016 | Open | `ConversationTurn` exists but `AgentSession` still uses `Message` internally. |
| FH-0020 | Open | `Model.Provider` remains mutable runtime state on catalog models. |
| FH-0024 | Open | No `ConfigManager` TOML round-trip tests found. |
| FH-0025 | Open/partial | Compatibility routing tests exist, but no direct `OpenCodeProviderTests` for `ResolveBaseUrl` / responses model set. |
| FH-0026 | ✅ Complete | `ProviderStateManager_ClearSession_EmitsOneEventPerKeyWithReason` exists. |
| FH-0027 | Open | No Azure `api-version` support found. |
| FH-0028 | Open | No config-defined custom model source found. |
| FH-0029 | Open | Tool-call validation feedback remains basic. |

### 3.3 Future Plans Not Yet Implemented

The prior report incorrectly listed several implemented plans here. Based on current source, the genuinely unstarted or largely unstarted future items are:

| Plan | Description | Current state |
|------|-------------|---------------|
| 0008.5 | Tree-sitter Core Infrastructure | Not found in source |
| 0008 | Markdown parsing and syntax highlighting | Content block placeholders exist, but parser/highlighter not implemented |
| 0009 | Sandboxing Foundations | Not implemented; distinct from completed Kitty keyboard plan also numbered 0009 |
| 0010 | Edit Harness v2 / Stateful Anchors | Not implemented |

---

## 4. Test Coverage Snapshot

| Metric | Value |
|--------|-------|
| Solution tests | **996 passing** |
| `Omicron.Core.Tests` test files | **46** `*Tests.cs` files |
| Build warnings/errors for solution | **0 / 0** |
| Out-of-solution generator test project | **2 passing** |

Representative coverage now includes:

- UTF-8 string/builder/composite formatting/generator tests.
- Binary/adaptive file reader and hex dump tests.
- Semantic content block and JSON round-trip tests.
- Workspace transaction and hashline edit harness tests.
- Provider state, replay hardening, and projection tests.
- Terminal backend, Kitty keyboard protocol, transcript viewport, and TUI session integration tests.
- Model metadata and compatibility detector tests.

---

## 5. String / UTF-8 Boundary Audit Update

The codebase is not string-free, and that is expected. Remaining `string` usage is mostly at boundaries:

- CLI/TUI input, display, key handling, and slash command output.
- JSON/tool argument extraction (`object?` / `JsonElement` to strings).
- Decoded document/content processors where libraries expose text as `string`.
- Terminal input parser paste/text events.
- Explicit `Utf8String.ToString()` / `GetTextString()` display or compatibility calls.
- The remaining `ToolResult` / `ContentProcessorResult` dual-state bridges.

The important cleanup target is not every `string`, but the remaining internal result models that carry both `string` and UTF-8 bytes.

---

## 6. Recommendations

1. **Migrate `ToolResult` and `ContentProcessorResult` to `Utf8String`** in one coordinated pass.
2. **Add `Omicron.Core.Generators.Tests` to the default solution/CI path** if it is intended to be part of normal validation.
3. **Update stale implementation-plan/backlog statuses** after deciding whether this report should be the source of truth or whether the older plan files should be patched too.
4. **Address FH-0029, FH-0028, and FH-0027** as the next user-visible reliability/configuration improvements.
5. **Use Plan 0008.5 / 0008, Plan 0010, and sandboxing as the next major feature tracks**, since the TUI and binary/content foundations are no longer merely future work.
