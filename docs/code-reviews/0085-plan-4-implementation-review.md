# Code Review 0085: Plan 4 — Transaction-Backed Edit Harness v1

**Status:** Closed — 4 of 5 findings resolved, 1 deferred (pre-existing FH-0008)  
**Date:** 2026-05-10  
**Build:** 0 errors, 439 tests passing

---

## Executive Summary

Plan 4 delivered a transaction-backed hashline edit harness with two tools:

- `read_file_hashlines` — reads a file with compact per-line hash anchors.
- `edit_file_hashline` — edits via hash-anchored validation, workspace transaction staging, diff generation, and auto-commit.

The implementation follows the updated non-cryptographic hashing guidance (FNV-1a, 2-letter lowercase anchors, ±5 rebase).

---

## Verification

```text
dotnet build → 0 errors
dotnet test  → 439 passing, 0 failed
```

---

## Files Changed

**Created:**
- `Omicron.Core/Tools/LineHash.cs`
- `Omicron.Core/IO/TextEncodingDetector.cs` *(follow-up)*
- `Omicron.Core.Tests/TransactionEditHarnessTests.cs`

**Modified:**
- `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs` *(+ follow-up + fix)*
- `Omicron.Core/Workspace/WorkspaceVfs.cs` *(follow-up)*
- `Omicron.Core/OmicronHost.cs`

---

## Findings Resolution Table

| # | Severity | Status | Description |
|---|----------|--------|-------------|
| 1 | Medium | **Resolved** | Binary detection inconsistency — `TextEncodingDetector` follow-up |
| 2 | Medium | **Resolved** | Missing multi-edit rebase test — 2 new tests added |
| 3 | Low | **Resolved** | Overly defensive edit parsing — stripped to single code path |
| 4 | Low | **Resolved** | Wrong anchor preview lines — replaced with message to call read_file_hashlines |
| 5 | Low | **Deferred** (FH-0008) | Transaction manager hard dependency — not a Plan 4 regression |

---

## Finding 1 — RESOLVED: Binary detection inconsistency

**Original issue:** `read_file_hashlines` used extension-based binary detection (`stat.IsBinary`); `edit_file_hashline` used extension check + ad-hoc null-byte scan. Inconsistent.

**Resolution:** Created `Omicron.Core/IO/TextEncodingDetector.cs` — a shared content-based text/binary detector (BOM check → null-byte scan → UTF-8 decode → printable ratio). Both tools now call `TextEncodingDetector.IsText(bytes.Span)`. Also integrated into `WorkspaceVfs.ReadDirectoryAsync` for directory listing binary detection.

Detection algorithm adapted from `StrikeCore.ChartParser.IO.ParsingTools.DetectEncoding`:
1. Empty file → text.
2. Null byte in first 8 KB → binary.
3. BOM present → text.
4. Strict UTF-8 decode succeeds → text.
5. Lenient UTF-8 decode (no U+FFFD) → text.
6. ≥85% printable ratio → text; otherwise binary.

---

## Finding 2 — RESOLVED: No test for multi-edit rebase

**Original issue:** No test proving bottom-up application correctly handles line shifts when one edit expands and shifts another edit's target.

**Resolution:** Added two tests:

- `EditHashline_MultiEdit_BottomUpRespectsShiftedLines` — edit at line 5 applied first (bottom-up), then edit at line 1 expands "a" → "X\nY\nZ", shifting former line 5 to line 7. Both edits succeed, final content correct.
- `EditHashline_MultiEdit_TwoEditsSameRegionDontInterfere` — two non-overlapping edits at lines 1 and 3 both apply correctly without interfering.

---

## Finding 3 — RESOLVED: Overly defensive edit parsing

**Original issue:** The `edit_file_hashline` handler had 4 code paths for parsing edits (JsonElement array, single dict, IEnumerable<object> of JsonElement, IEnumerable<object> of IReadOnlyDictionary). Three were dead code.

**Resolution:** Stripped all paths except `JsonElement` array. Error message updated to name required fields explicitly. Test helper updated to serialize edits through JSON, exercising the real code path.

---

## Finding 4 — RESOLVED: Wrong anchor preview lines

**Original issue:** Post-commit output showed anchors for lines 1–5 regardless of edit location. An edit at line 100 would show unrelated anchors.

**Resolution:** Removed the first-5-lines anchor preview entirely. Post-commit output now tells the model to call `read_file_hashlines` for fresh anchors.

---

## Finding 5 — DEFERRED: Transaction manager hard dependency

**Original issue:** `WorkspaceTransactionManager` requires `HostWorkspaceFileSystem` (pre-existing, FH-0008). Not a Plan 4 regression.

**Status:** Deferred. No action for Plan 4.

---

## Follow-up: Content-Based Binary Detection

See Finding 1 resolution above. Files changed in follow-up:

| File | Change |
|------|--------|
| `Omicron.Core/IO/TextEncodingDetector.cs` | **New** — content-based text/binary detector |
| `Omicron.Core/Workspace/WorkspaceVfs.cs` | `ReadDirectoryAsync` uses `TextEncodingDetector.IsTextFile()` |
| `Omicron.Core/Extensions/BuiltinWorkspaceToolsExtension.cs` | Both tools use `TextEncodingDetector.IsText()` |
| `Omicron.Core.Tests/TransactionEditHarnessTests.cs` | Binary tests use actual binary bytes |

---

## Plan 4 Closure

All acceptance criteria met. All actionable review findings resolved. Plan 4 is closed.
