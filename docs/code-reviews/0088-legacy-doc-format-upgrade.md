# Code Review 0088: Legacy Document Format System Upgrade

**Status:** Accepted — 2 low findings  
**Date:** 2026-05-10  
**Build:** 0 errors, 505 tests passing

---

## What Changed

### LegacyOfficeProcessor — complete rewrite

Replaced NPOI with **DocSharp** (0.18.1, 3 packages: `.Doc`, `.Xls`, `.Ppt`). Three-stage pipeline:

| Stage | Method | What it does |
|-------|--------|-------------|
| 1 | DocSharp conversion | `.doc` → `.docx`, `.xls` → `.xlsx`, `.ppt` → `.pptx`, then delegates to `OpenXmlProcessor` for text extraction |
| 2 | OLE2 binary scan | Direct UTF-16LE text scanning from OLE2 compound document streams (no library needed) |
| 3 | Hex dump | Universal fallback |

**Architecture:** DocSharp converts legacy binary → OpenXML, then `OpenXmlProcessor` handles the extraction. This is cleaner than NPOI's mixed API (different read patterns per format). Each converter has proper temp file cleanup in `finally` blocks.

### OpenXmlProcessor — restructured

- **Single `MemoryStream`** reused across DOCX → XLSX → PPTX attempts (reset via `Position = 0` between tries). No redundant stream allocations.
- **ZIP-based fallback** handles `word/document.xml`, `xl/sharedStrings.xml`, `xl/worksheets/sheet*.xml`, `ppt/slides/slide*.xml`.
- **XML entity decoding** handles `&amp;`, `&lt;`, `&gt;`, `&quot;`, `&apos;`, hex (`&#xNNNN;`), decimal (`&#NNNN;`).
- **Whitespace collapse** via compiled `Regex` for cleaner output.

### Other

- NPOI removed from `.csproj`, DocSharp added (3 packages).
- `AgentSession` null-coalesces `resultText` → `""` for tool results (defensive fix).

---

## Findings

### Finding 1: `Path.GetTempFileName()` can throw under high temp-file load

**Severity:** Low  
**File:** `LegacyOfficeProcessor.cs` (all three conversion methods)

`Path.GetTempFileName()` throws `IOException` when more than 65,535 temp files exist in the temp directory. Each conversion creates two temp files (input + output).

**Fix:** Replace with `Path.Combine(Path.GetTempPath(), $"omicron_legacy_{Guid.NewGuid():N}.tmp")` which never throws on name collision. Same cleanup pattern in `finally`.

### Finding 2: Redundant null-forgiving operator on `ms`

**Severity:** Low (cosmetic)  
**File:** `OpenXmlProcessor.cs` (lines ~80, ~95)

```csharp
ms!.Position = 0;
```

`ms` is declared as `using var ms = new MemoryStream(...)` directly before the `try` block. C# nullable flow analysis knows it's not null. The `!` is unnecessary.

**Fix:** Remove `!` — just `ms.Position = 0;`.

---

## Verdict

The DocSharp conversion pipeline is a solid architectural upgrade over NPOI. Converting legacy formats to OpenXML then delegating to the same `OpenXmlProcessor` avoids format-specific extraction logic. The two findings are cosmetic/minor.
