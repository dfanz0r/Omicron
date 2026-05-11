# Review: Plans 0007–0010 — Forward Architecture Audit

**Date:** 2026-05-10

---

## Plan Summary

| Plan | Title | Size | Scope |
|------|-------|:----:|-------|
| 0007 | Terminal Backend and Frame Buffer | ~700 lines | UTF-8 store, cell-width, terminal backend, frame buffer, diff renderer, ANSI encoder, TUI shell |
| 0007.1 | Transcript Viewport and App Layout | ~500 lines | Transcript virtualization, viewport scrollback, widget layout system |
| 0008 | Markdown Parsing and Syntax Highlighting | ~540 lines | Incremental markdown parser, 15 regex tokenizers, highlight cache |
| 0009 | Sandboxing Foundations | ~660 lines | Policy model, risk classifier, broker integration, audit events |
| 0010 | Edit Harness v2 — Stateful Anchors | ~680 lines | Dirac-style anchors, Myers diff reconciliation, multi-file batches, C# AST spike |

---

## What's Good

### Cross-cutting

- **All plans reference real RFCs.** Every `Primary RFCs` section points to files that exist in `docs/rfcs/`.
- **Dependencies are well-specified.** Each plan lists predecessor plans. Plan 10 correctly depends on Plan 4 (hashline edit), Plan 3.1 (Myers diff), and Plan 9 (sandboxing risk classification).
- **Non-goals sections are thorough.** Each plan explicitly excludes related concerns to prevent scope creep.
- **Detailed type signatures.** Plans 7 and 7.1 include full C# signatures for most types, not just descriptions.

### Plan 7 specifics

- **`GlyphRef` with ASCII fast-path** is well-designed. One `uint` carries either an ASCII byte inline or an intern-table reference.
- **Platform-specific raw mode** documented for both Windows (`GetConsoleMode`/`SetConsoleMode`) and Unix (`tcgetattr`).
- **`TerminalLifecycle` with multiple safety nets** (`Dispose`, `AppDomain.ProcessExit`, `CancelKeyPress`) — critical for crash recovery.
- **Performance targets** are concrete and measurable (16ms/frame, <2ms diff render, <100ms for 100k lines).

### Plan 8 specifics

- **Incremental parser design** is correct for streaming LLM output — parse chunk by chunk, resume from safe commit points.
- **Fallback-first strategy** is right: regex tokenizers now, Tree-sitter as future optimization.
- **15 languages** covered by regex tokenizers — pragmatic for an MVP.

### Plan 9 specifics

- **Deny-by-default policy** is the right posture.
- **Workspace overlays as the first sandbox mechanism** is correct — even without OS sandboxing, writes go through transactions.
- **Risk classifier** with explanations is good for user-facing permission prompts.

### Plan 10 specifics

- **Myers diff reconciliation** using the existing Plan 3.1 diff engine is correct.
- **Anchor state per file** tracked across edits — matches Dirac's design.
- **AST spike scoped to C# only** via Roslyn — avoids overcommitting.

---

## Issues

### Plan 7 — Underspecified `GlyphInternTable`

The intern table is described but the eviction policy is vague ("cap table size, evict LRU if needed"). The `GlyphRef.Interned` constructor takes an `int internId` which assumes stable IDs. If eviction happens, existing `GlyphRef` values in the frame buffer become dangling references. Either:

- Document that intern IDs are stable and the table never evicts (grow only), or
- Make eviction explicit with `RenderCell` carrying the UTF-8 bytes and `GlyphRef` being purely a render-time optimization.

### Plan 7 — `CellWidthCalculator` table duplication risk

The plan says "hardcoded tables" for Unicode width. `System.Globalization.StringInfo` can provide grapheme boundaries, but there's no built-in .NET API for terminal cell width. The plan should note which Unicode version's East Asian Width tables to use (likely Unicode 15.1) and where the data comes from (potentially embedding a generated `UnicodeWidth.table.cs` from the Unicode Character Database).

### Plan 8 — 15 regex tokenizers is ambitious

Each tokenizer needs keywords, operators, string/comment detection, and nesting awareness (for multiline strings/comments). Regex alone is fragile for languages with significant nesting (HTML, Markdown). The plan should identify which languages are regex-adequate vs which may produce visibly wrong highlighting. Consider marking CSS, HTML, and Markdown as "fallback: plain text with code fence only" rather than trying to regex-parse HTML tags correctly.

### Plan 9 — `LocalNoSandboxProvider` naming

The "no sandbox" provider applies policy (filesystem allowlist, network deny) but doesn't use OS-level sandboxing. The name `LocalNoSandboxProvider` makes it sound like it does nothing. Rename to `PolicyEnforcingProvider` or `WorkspaceOnlyProvider` to reflect that it enforces policy without OS isolation.

### Plan 9 — Missing dependency on Plan 4

Plan 9 says "Depends On: Plans 1–6, Plan 4, Plan 3.6." But `edit_file_hashline` (Plan 4) should also go through the sandbox policy — the plan should explicitly mention that the edit harness integrates with the sandbox broker, not just that it "depends on" plan 4 as a predecessor.

### Plan 10 — Depends on unimplemented Plan 9

Plan 10 lists Plan 9 (sandboxing) as a dependency. If Plan 10 is implemented before Plan 9, the risk classifier integration won't exist. Either:

- Remove Plan 9 as a hard dependency (the edit harness uses workspace transactions directly regardless of sandbox), or
- Note that the first PR of Plan 10 should work without Plan 9, with sandbox integration as a follow-up.

### Plan 7 — Missing mention of `write_file` tool

Plan 7 references Plan 5 `ContentBlock` model but doesn't mention the `write_file` tool we just added. The TUI input editor should support creating new files. This is minor — the TUI shell will just use whatever tools exist.

---

## Verdict

All five plans are well-structured, grounded in existing RFCs, and have realistic scopes with good non-goals sections. The issues above are minor clarifications. No plan contradicts another. The dependency chain is correct:

```
7 (Terminal) → 7.1 (Viewport) → 8 (Markdown)
                              → 9 (Sandbox)
4 (Edit v1)   → 10 (Edit v2) → 9 (Sandbox)
```
