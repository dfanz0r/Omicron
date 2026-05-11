# Implementation Plan 0010: Edit Harness v2 — Stateful Anchors and Multi-File Batches

Status: Proposed
Target: Upgrade the edit harness from stateless hashline anchors to Dirac-style stateful single-token anchors, multi-file batch support, and optional AST context curation.

## Purpose

Plan 4 introduced the hashline edit tool: stateless line-number + short-hash anchors. This is robust for small edits but token-inefficient for multi-edit sessions because models must reproduce line numbers and hashes for every edit. Plan 10 upgrades to:

1. **Stateful single-token anchors** — stable labels that survive line shifts.
2. **Anchor state manager** — tracks anchor tables per file, reconciles after edits.
3. **Myers diff reconciler** — preserves anchors for unchanged lines, assigns new anchors to changed lines.
4. **Multi-file batch edits** — atomic transactions across multiple files.
5. **AST context curation spike** — Roslyn-based symbol index for C# files.

This follows RFC 0012 and lessons from Dirac's hash-anchor + Myers diff design.

## Primary RFCs

- RFC 0012 — Agent Orchestration, VFS Locking, and Edit Harness
- RFC 0006 — Persistence, Session History, VFS, and Workspace Snapshots
- RFC 0001 — Core Architecture and Event Model

## Depends On

- Plan 4 (`edit_file_hashline`, `LineHash`, workspace transactions)
- Plan 3.1 (Myers diff engine)
- Plan 3.25 (`IWorkspaceTransactionManager`)
- Plan 8.5 (`TreeSitterClient`, `ParserManager`, `QueryManager` — for semantic analysis of all languages including C#)
- Plan 9 (sandboxing foundations — optional soft dependency; first PR works without it)

**Plan 8.5 dependency note:** Tree-sitter is the unified parsing infrastructure for all languages. Roslyn is deferred to a future task — tree-sitter C# grammars exist and are sufficient for MVP semantic analysis.

**Plan 9 dependency note:** Plan 10 can be implemented before Plan 9 is complete. The edit harness uses `IWorkspaceTransaction` directly for atomicity. Plan 9's sandboxing adds risk classification, permission prompts, and audit events on top — these are enhancements, not blockers. The first PR for Plan 10 should:
1. Use `IWorkspaceTransactionManager.BeginTransaction()` directly.
2. Skip risk classification if `SandboxPolicyRegistry` is not yet wired.
3. Emit standard `ToolInvocationStartedEvent` / `ToolInvocationCompletedEvent` via `IEventSink`.
4. Add Plan 9 integration (permission gating, audit events) in a follow-up PR once Plan 9 is available.

## Non-Goals

- No IDE-style refactoring (rename symbol, extract method, etc.)
- No symbol-level locks (VFS file locks only)
- No remote workspace edits (local workspace only)
- No automatic rebase / merge conflict resolution
- No three-way merge
- No Roslyn integration (deferred; tree-sitter C# grammar covers MVP needs)

## Guiding Principles

1. **Anchors are backend state, not model context.** The model receives anchor labels; the harness validates them against the anchor table.
2. **Preserve unchanged anchors.** After an edit, lines that did not change keep their anchors.
3. **Assign new anchors to changed lines only.** Minimize anchor table churn.
4. **Batch edits are atomic.** All files in a batch share one workspace transaction.
5. **Errors are model-actionable.** Stale anchors return current nearby anchors + context.

---

## Proposed Layout

```text
Omicron.Core/Tools/EditHarness/
  AnchorTable.cs
  AnchorId.cs
  AnchorStateManager.cs
  IAnchorReconciler.cs
  MyersAnchorReconciler.cs
  EditValidationResult.cs
  StatefulAnchorEditTool.cs
  MultiFileBatchEditTool.cs
  ModelActionableError.cs

Omicron.Core/Tools/EditHarness/Ast/
  IAstContextProvider.cs
  TreeSitterAstContextProvider.cs
  SymbolIndex.cs
  SymbolAnchor.cs
  SymbolDescriptor.cs
  SymbolReference.cs
  SymbolIndexService.cs

Omicron.Core.Tests/EditHarness/
  AnchorTableTests.cs
  AnchorReconcilerTests.cs
  StatefulAnchorEditToolTests.cs
  MultiFileBatchEditToolTests.cs
  TreeSitterAstContextProviderTests.cs
```

---

## Phase A: Anchor State Manager

### Goals
Track file → line → anchor mappings. Reconcile after edits or external file changes.

### Deliverables

#### A1. `AnchorId`
```csharp
namespace Omicron.Core.Tools.EditHarness;

/// <summary>A stable single-token anchor label.</summary>
public readonly record struct AnchorId(string Value)
{
    private static readonly Random Random = new();
    private static readonly char[] Alphabet = "abcdefghijklmnopqrstuvwxyz".ToCharArray();

    public static AnchorId Generate()
    {
        // Generate a 4-letter lowercase anchor.
        // Future: use BPE-optimized bigram table per oh-my-pi research.
        var chars = new char[4];
        for (int i = 0; i < 4; i++) chars[i] = Alphabet[Random.Next(Alphabet.Length)];
        return new AnchorId(new string(chars));
    }

    public static AnchorId FromHash(uint hash)
    {
        // Deterministic mapping from a 32-bit hash to a short anchor.
        // Map to base-26 letters: 4 chars = 26^4 = 456,976 possibilities.
        int v = (int)(hash % 456976);
        var chars = new char[4];
        for (int i = 3; i >= 0; i--)
        {
            chars[i] = Alphabet[v % 26];
            v /= 26;
        }
        return new AnchorId(new string(chars));
    }
}
```

Design notes:
- Pure lowercase letters avoid ambiguity with line numbers.
- 4 chars gives ~457k labels. For files <10k lines, collision probability is negligible.
- `FromHash` allows deterministic anchor generation from content hash.

#### A2. `AnchorLineEntry`
```csharp
namespace Omicron.Core.Tools.EditHarness;

public readonly record struct AnchorLineEntry(
    AnchorId Anchor,
    long ByteStart,
    int ByteLength,
    uint ContentHash,    // xxHash32 or FNV-1a of normalized line text
    string NormalizedText);
```

#### A3. `AnchorTable`
```csharp
namespace Omicron.Core.Tools.EditHarness;

public sealed class AnchorTable
{
    public WorkspacePath Path { get; }
    public long Version { get; private set; }
    public IReadOnlyList<AnchorLineEntry> Lines => _lines;

    public AnchorTable(WorkspacePath path);

    /// <summary>Build or rebuild the anchor table from file content.</summary>
    public void Rebuild(ReadOnlyMemory<byte> utf8Content);

    /// <summary>Find the line entry for an anchor.</summary>
    public AnchorLineEntry? FindLine(AnchorId anchor);

    /// <summary>Find the anchor for a given line index.</summary>
    public AnchorLineEntry? FindByLineIndex(int lineIndex);

    /// <summary>Update the table after an edit, preserving unchanged anchors.</summary>
    public void ApplyReconciliation(IReadOnlyList<AnchorLineEntry> newLines);

    public void IncrementVersion() => Version++;
}
```

Rebuild algorithm:
1. Split content into lines using `TextLineSplitter`.
2. For each line:
   - Normalize: trim trailing `\r`, replace tabs with 4 spaces (or keep tabs? document decision).
   - Compute `ContentHash` = FNV-1a or xxHash32 over normalized UTF-8 bytes.
   - Generate `AnchorId` = `AnchorId.FromHash(hash)`.
   - Record `(anchor, byteStart, byteLength, hash, normalizedText)`.
3. If deterministic hash collision occurs (same anchor for different lines), append a disambiguation suffix or rehash with salt.

#### A4. `AnchorStateManager`
```csharp
namespace Omicron.Core.Tools.EditHarness;

public sealed class AnchorStateManager
{
    private readonly Dictionary<WorkspacePath, AnchorTable> _tables = new();

    public AnchorTable GetOrCreateTable(WorkspacePath path, IWorkspaceFileSystem fs);
    public void InvalidatePath(WorkspacePath path);
    public void ReconcilePath(WorkspacePath path, IWorkspaceFileSystem fs);
}
```

- `GetOrCreateTable` reads the file from VFS and builds the table if not cached.
- `InvalidatePath` drops the cached table (called before external file changes are known).
- `ReconcilePath` rebuilds the table and attempts to preserve anchors from the old table using `MyersAnchorReconciler`.

### Tests
- `AnchorId_Generate_IsLowercaseFourChars`
- `AnchorId_FromHash_IsDeterministic`
- `AnchorTable_Rebuild_CorrectLineCount`
- `AnchorTable_FindLine_ReturnsCorrectEntry`
- `AnchorTable_FindByLineIndex_ReturnsCorrectEntry`
- `AnchorTable_Rebuild_DeterministicAnchors`
- `AnchorStateManager_GetOrCreate_BuildsFromFile`
- `AnchorStateManager_Invalidate_DropsCache`

### Acceptance Criteria
- Anchor table rebuilds from file content deterministically.
- Each line gets a unique anchor for typical file sizes (<10k lines).
- Anchor state manager caches tables and rebuilds on demand.

---

## Phase B: Myers Anchor Reconciler

### Goals
After an edit (or external file change), reconcile the old anchor table with the new file content so unchanged lines keep their anchors.

### Deliverables

#### B1. `IAnchorReconciler`
```csharp
namespace Omicron.Core.Tools.EditHarness;

public interface IAnchorReconciler
{
    IReadOnlyList<AnchorLineEntry> Reconcile(
        IReadOnlyList<AnchorLineEntry> oldLines,
        IReadOnlyList<AnchorLineEntry> newLines);
}
```

#### B2. `MyersAnchorReconciler`
Uses the existing `TextDiffEngine` (Plan 3.1) to diff old lines vs new lines by content hash:

```csharp
namespace Omicron.Core.Tools.EditHarness;

public sealed class MyersAnchorReconciler : IAnchorReconciler
{
    public IReadOnlyList<AnchorLineEntry> Reconcile(
        IReadOnlyList<AnchorLineEntry> oldLines,
        IReadOnlyList<AnchorLineEntry> newLines)
    {
        // 1. Diff old normalized text lines vs new normalized text lines.
        // 2. For each matched (unchanged) line, preserve the old anchor.
        // 3. For inserted lines, generate new anchors.
        // 4. For deleted lines, drop anchors.
        // 5. Return the merged list.
    }
}
```

Algorithm:
1. Extract `NormalizedText` from both `oldLines` and `newLines`.
2. Call `TextDiffEngine.DiffLines(oldTexts, newTexts)`.
3. Walk the edit script:
   - Context lines → copy old `AnchorLineEntry`.
   - Deleted lines → skip.
   - Inserted lines → create new `AnchorLineEntry` with fresh anchor from hash.
   - Replaced lines → create new entries (even if one old line maps to one new line with different text).
4. Return the reconstructed list.

Edge case: if a line's text changed but its hash collision accidentally produces the same anchor, the reconciler still treats it as a replacement because the diff says "replace."

### Tests
- `Reconciler_NoChanges_PreservesAllAnchors`
- `Reconciler_InsertLine_NewAnchorAssigned`
- `Reconciler_DeleteLine_OldAnchorDropped`
- `Reconciler_ReplaceLine_NewAnchorAssigned`
- `Reconciler_MoveBlock_AnchorsPreservedForUnchangedLines`
- `Reconciler_MultipleEdits_CorrectResult`

### Acceptance Criteria
- Unchanged lines keep their anchors after reconciliation.
- Inserted lines get new anchors.
- Deleted lines lose their anchors.
- Replaced lines get new anchors.
- Performance: reconcile 1k lines in <20 ms.

---

## Phase C: Stateful Anchor Edit Tool

### Goals
Replace or extend `edit_file_hashline` with an anchor-based tool where the model submits anchor labels instead of line numbers.

### Deliverables

#### C1. `edit_file_anchors` tool
```json
{
  "name": "edit_file_anchors",
  "description": "Edit a file using stable single-token anchors. First read the file with read_file_anchors to get anchor labels.",
  "parameters": {
    "type": "object",
    "properties": {
      "path": { "type": "string" },
      "edits": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "start_anchor": { "type": "string", "description": "Anchor id of the line where the edit starts" },
            "end_anchor": { "type": "string", "description": "Anchor id of the line where the edit ends (inclusive)" },
            "old_text": { "type": "string", "description": "Exact old text to replace (validation guard)" },
            "new_text": { "type": "string", "description": "Replacement text" }
          },
          "required": ["start_anchor", "end_anchor", "new_text"]
        }
      }
    },
    "required": ["path", "edits"]
  }
}
```

#### C2. `read_file_anchors` tool
```json
{
  "name": "read_file_anchors",
  "description": "Read a file with stable anchor labels on each line. Use these anchors with edit_file_anchors.",
  "parameters": {
    "type": "object",
    "properties": {
      "path": { "type": "string" },
      "offset": { "type": "integer" },
      "limit": { "type": "integer" }
    },
    "required": ["path"]
  }
}
```

Output format:
```text
[FILE] src/Foo.cs
   1  abcd | using System;
   2  efgh | namespace Omicron;
   3  ijkl | public class Foo
   4  mnop | {
   5  qrst |     public int Bar;
   6  uvwx | }
```

Each line: `lineNumber anchorId | content`
- Anchor is separated by a delimiter (space-pipe-space).
- Model uses anchor id, not line number, for edits.

#### C3. `StatefulAnchorEditTool`
```csharp
namespace Omicron.Core.Tools.EditHarness;

public sealed class StatefulAnchorEditTool : ITool
{
    public string Name => "edit_file_anchors";

    public async Task<ToolResult> ExecuteAsync(ToolInvocationContext ctx);
}
```

Execution flow:
1. Resolve path through workspace VFS.
2. Read file bytes.
3. Get or build `AnchorTable` via `AnchorStateManager`.
4. Validate each edit:
   a. `start_anchor` and `end_anchor` must exist in the table.
   b. `old_text` (if provided) must match the current text between the anchors.
   c. Edits must be non-overlapping.
5. Apply edits to an in-memory buffer:
   a. Replace text between start and end anchors.
   b. If `old_text` is provided and doesn't match, return stale-anchor error.
6. Write result to a workspace transaction.
7. Generate diff via `tx.GetDiffAsync()`.
8. Commit transaction.
9. Reconcile anchor table:
   a. Run `MyersAnchorReconciler` on old vs new content.
   b. Update the cached `AnchorTable`.
   c. Increment version.
10. Return diff + updated anchors.

#### C4. Stale anchor error format
```json
{
  "success": false,
  "error": "stale_anchor",
  "details": {
    "path": "src/Foo.cs",
    "failed_edit": {
      "start_anchor": "qrst",
      "expected_text": "    public int Bar;",
      "current_text": "    public string Baz;"
    },
    "nearby_anchors": [
      { "line": 3, "anchor": "ijkl", "text": "public class Foo" },
      { "line": 4, "anchor": "mnop", "text": "{" },
      { "line": 5, "anchor": "abca", "text": "    public string Baz;" },
      { "line": 6, "anchor": "uvwx", "text": "}" }
    ],
    "suggestion": "The file has changed since you last read it. Re-read with read_file_anchors and retry with updated anchors."
  }
}
```

#### C5. `read_file_anchors` implementation
```csharp
public sealed class ReadFileAnchorsTool : ITool
{
    public string Name => "read_file_anchors";

    public async Task<ToolResult> ExecuteAsync(ToolInvocationContext ctx)
    {
        // 1. Read file via VFS.
        // 2. Build AnchorTable.
        // 3. Format lines with line numbers, anchors, and content.
        // 4. Apply offset/limit truncation.
        // 5. Return formatted text.
    }
}
```

### Tests
- `EditTool_SingleEdit_Success`
- `EditTool_MultipleEdits_Success`
- `EditTool_StaleAnchor_ReturnsErrorWithNearbyAnchors`
- `EditTool_OldTextMismatch_ReturnsError`
- `EditTool_OverlappingEdits_ReturnsError`
- `EditTool_BinaryFile_ReturnsError`
- `EditTool_PathOutsideWorkspace_ReturnsError`
- `EditTool_CommitsTransaction`
- `ReadTool_ReturnsAnchors`
- `ReadTool_OffsetLimit_Truncates`

### Acceptance Criteria
- `edit_file_anchors` applies valid edits transactionally.
- Stale anchors return model-actionable errors with nearby current anchors.
- `old_text` mismatch is caught before mutation.
- Overlapping edits are rejected.
- Binary files are rejected.
- Transaction commit creates a workspace snapshot.
- Anchor table is reconciled and updated after commit.

---

## Phase D: Multi-File Batch Edits

### Goals
Support coordinated edits across multiple files in one atomic transaction.

### Deliverables

#### D1. `edit_files_batch` tool
```json
{
  "name": "edit_files_batch",
  "description": "Apply edits to multiple files in a single atomic transaction.",
  "parameters": {
    "type": "object",
    "properties": {
      "edits": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "path": { "type": "string" },
            "edits": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "start_anchor": { "type": "string" },
                  "end_anchor": { "type": "string" },
                  "old_text": { "type": "string" },
                  "new_text": { "type": "string" }
                },
                "required": ["start_anchor", "end_anchor", "new_text"]
              }
            }
          },
          "required": ["path", "edits"]
        }
      }
    },
    "required": ["edits"]
  }
}
```

#### D2. `MultiFileBatchEditTool`
```csharp
namespace Omicron.Core.Tools.EditHarness;

public sealed class MultiFileBatchEditTool : ITool
{
    public string Name => "edit_files_batch";

    public async Task<ToolResult> ExecuteAsync(ToolInvocationContext ctx);
}
```

Execution flow:
1. Acquire file locks in deterministic path order (alphabetical) to prevent deadlocks.
2. Validate all edits for all files (stale anchors, old_text mismatch, overlapping).
3. If any validation fails, return a batch error listing all failures; do not apply any edits.
4. Open one `IWorkspaceTransaction`.
5. Apply edits to transaction overlay for each file.
6. Generate combined diff.
7. Commit transaction.
8. Reconcile anchor tables for all modified files.
9. Return combined diff.

Lock ordering rule:
- Sort file paths alphabetically.
- Acquire read locks for files being read (anchor validation).
- Acquire write locks for files being edited.
- If a file is both read and edited, acquire write lock.
- If lock acquisition fails (timeout), return error suggesting retry.

#### D3. `IWorkspaceLockManager` (minimal)
```csharp
namespace Omicron.Core.Workspace;

public interface IWorkspaceLockManager
{
    ValueTask<IWorkspaceLockLease> AcquireAsync(WorkspacePath path, WorkspaceLockKind kind, CancellationToken ct);
    ValueTask ReleaseAsync(IWorkspaceLockLease lease);
}

public enum WorkspaceLockKind { Read, Write }

public interface IWorkspaceLockLease : IDisposable
{
    WorkspacePath Path { get; }
    WorkspaceLockKind Kind { get; }
}
```

MVP: implement as an in-process `SemaphoreSlim` per path. No cross-process locking.

### Tests
- `BatchTool_TwoFiles_Success`
- `BatchTool_OneFileFails_ValidationFailsAll`
- `BatchTool_LockTimeout_ReturnsRetryError`
- `BatchTool_DeterministicOrder_PreventsDeadlock`
- `BatchTool_CombinedDiff_ShowsBothFiles`
- `BatchTool_Rollback_NoChangesOnFailure`

### Acceptance Criteria
- Multi-file batch edits are atomic: all succeed or none apply.
- Validation errors list every failed edit, not just the first.
- File locks are acquired in deterministic order.
- Lock timeout returns a retry suggestion.
- Combined diff shows changes from all files.

---

## Phase E: AST Context Curation Spike — Tree-Sitter

### Goals
Build symbol indexes for code understanding. Use **Roslyn for C#** (perfect accuracy) and **tree-sitter for cross-language** support (broad coverage). Both feed into a unified `SymbolIndexService`.

### Deliverables

#### E1. `RoslynAstContextProvider` (Track A: C#)
```csharp
namespace Omicron.Core.Tools.EditHarness.Ast;

public sealed class RoslynAstContextProvider : IAstContextProvider
{
    public string Language => "csharp";

    /// <summary>Build a symbol index from C# source text.</summary>
    public SymbolIndex BuildIndex(WorkspacePath path, string sourceText);

    /// <summary>Get minimal relevant symbols for a given line or anchor.</summary>
    public IReadOnlyList<SymbolInfo> GetContext(SymbolIndex index, int lineNumber, int contextLines = 5);
}

public sealed record SymbolInfo(
    string Name,
    string Kind,        // "class", "method", "property", "field", "namespace"
    int LineStart,
    int LineEnd,
    string? ContainingType);

public sealed class SymbolIndex
{
    public IReadOnlyList<SymbolInfo> Symbols { get; }
    public SymbolInfo? FindSymbolAtLine(int lineNumber);
}
```

Implementation:
- Use `Microsoft.CodeAnalysis.CSharp` (Roslyn) to parse the syntax tree.
- Walk the tree for type declarations, method declarations, property declarations, field declarations.
- Record line spans and containing type names.
- No semantic analysis required for MVP; syntax-only is sufficient.

#### E2. `TreeSitterAstContextProvider` (Track B: All languages)
```csharp
namespace Omicron.Core.Tools.EditHarness.Ast;

public sealed class TreeSitterAstContextProvider : IAstContextProvider
{
    public IReadOnlyList<string> SupportedLanguages { get; }

    public SymbolIndex BuildIndex(WorkspacePath path, string language, ReadOnlyMemory<byte> source);
    public IReadOnlyList<SymbolInfo> GetContext(SymbolIndex index, int lineNumber, int contextLines = 5);
}
```

Implementation:
- Uses the same `TreeSitterClient` from Plan 8 Phase F.
- Loads `definitions.scm` and `references.scm` query files per language.
- Follows Dirac's query naming convention:
  - `@name.definition.{kind}` — identifier node
  - `@{definition}.{kind}` — encompassing declaration node
  - `@name.reference` — identifier usage
  - `@doc` — preceding documentation comment
- Supports all languages where tree-sitter parsers and query files exist.

#### E3. `SymbolIndexService`
```csharp
namespace Omicron.Core.Tools.EditHarness.Ast;

public sealed class SymbolIndexService
{
    public SymbolIndexService(
        IWorkspaceFileSystem workspace,
        RoslynAstContextProvider? roslyn,
        TreeSitterAstContextProvider? treeSitter);

    public async Task IndexWorkspaceAsync(CancellationToken ct);
    public IReadOnlyList<SymbolDescriptor> GetDefinitions(WorkspacePath path);
    public IReadOnlyList<SymbolReference> FindReferences(string symbolName, string? scopePath);
    public IReadOnlyList<SymbolDescriptor> SearchSymbolsAsync(string query, string? scopePath);
}

public sealed record SymbolDescriptor(
    string Name,
    string Kind,
    WorkspacePath Path,
    int LineStart,
    int LineEnd,
    string? ContainingType,
    string? Documentation);

public sealed record SymbolReference(
    string SymbolName,
    WorkspacePath Path,
    int Line,
    int Column);
```

Following Dirac's model:
- Persistent JSON index: `FileIndexEntry { mtime, size, hash, symbols: [...] }`.
- Batch scan with concurrency limit.
- Exclude `node_modules`, `.git`, build directories.
- Re-index on file change detection (hash mismatch).

#### E4. `read_file_anchors` AST enhancement
Optionally include symbol context in the read output:
```text
[FILE] src/Foo.cs
   1  abcd | using System;
   2  efgh | namespace Omicron;
   3  ijkl | public class Foo
   4  mnop | {
   5  qrst |     // Symbol: Foo.Bar (property)
   6  uvwx |     public int Bar { get; set; }
   7  yzab | }
```

MVP: keep it simple. Just annotate lines that start a symbol with a comment hint.

#### E5. Symbol-level anchors (experimental)
Map symbol names to anchor ranges:
```csharp
public readonly record struct SymbolAnchor(
    SymbolInfo Symbol,
    AnchorId StartAnchor,
    AnchorId EndAnchor);
```

This allows the model to reference a symbol by name instead of anchor:
```json
{ "path": "src/Foo.cs", "symbol": "Foo.Bar", "new_text": "public string Bar { get; set; }" }
```

MVP: do not expose this to the model yet. Build the index and test it internally.

### Tests
- `RoslynIndex_Build_CorrectSymbolCount`
- `RoslynIndex_FindSymbolAtLine_ReturnsCorrectSymbol`
- `RoslynIndex_GetContext_ReturnsRelevantSymbols`
- `RoslynIndex_InvalidSyntax_DoesNotThrow`
- `TreeSitterIndex_Build_CorrectSymbolCount_ForTypescript`
- `TreeSitterIndex_Build_CorrectSymbolCount_ForPython`
- `TreeSitterIndex_FindReferences_ReturnsCrossFileRefs`
- `SymbolIndexService_IndexWorkspace_PopulatesIndex`
- `SymbolIndexService_SearchSymbols_ReturnsMatches`
- `SymbolIndexService_FindReferences_ReturnsRefs`

### Acceptance Criteria
- Roslyn parser builds a symbol index for valid C# files.
- Tree-sitter parser builds symbol indexes for TypeScript and Python files.
- Index handles files with minor syntax errors gracefully.
- Context query returns symbols near the requested line.
- `SymbolIndexService` indexes the workspace and supports search + reference lookup.
- Performance: index 1k-line file in <100 ms (Roslyn) or <200 ms (tree-sitter via worker).

---

## Performance Targets

| Scenario | Target |
|----------|--------|
| Build anchor table for 1k lines | <10 ms |
| Reconcile anchors after 10-line edit | <5 ms |
| Validate single edit | <2 ms |
| Multi-file batch (5 files, 10 edits) | <50 ms |
| Roslyn symbol index (1k lines) | <100 ms |

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Anchor collision in large files | Use 5-char anchors or hash+salt for files >5k lines. |
| Model confuses anchor with line number | Delimiter format is explicit (`anchor | content`); train in system prompt. |
| Reconciler is wrong after complex multi-line edits | Extensive diff tests; fallback to full rebuild if reconciliation fails. |
| Batch edit failure leaves no diagnostic | Return detailed per-file, per-edit error list. |
| Roslyn dependency is heavy | Make `Microsoft.CodeAnalysis.CSharp` an optional package; skip AST spike if unavailable. |
| External file changes invalidate anchors silently | Watch file system or detect hash mismatch on read; suggest re-read. |

---

## Definition of Done

- `AnchorTable` builds deterministic single-token anchors per line.
- `MyersAnchorReconciler` preserves anchors for unchanged lines after edits.
- `edit_file_anchors` tool accepts anchor-based edits and validates them.
- Stale anchors return model-actionable errors with nearby current anchors.
- `edit_files_batch` applies multi-file edits atomically with deterministic lock ordering.
- Workspace transaction integration: all edits go through `IWorkspaceTransaction`.
- Roslyn AST context provider builds symbol indexes for C# files.
- Tree-sitter AST context provider builds symbol indexes for TypeScript, Python, and other supported languages.
- `SymbolIndexService` indexes workspace, supports search, and resolves cross-file references.
- All tests pass with 0 warnings.
