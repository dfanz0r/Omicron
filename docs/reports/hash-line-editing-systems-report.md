# Hash Line Editing / Anchoring Systems in Agent CLI Tools

**Date:** 2026-05-10  
**Scope:** Analysis of hash-based line editing and anchoring systems across 7+ agent CLI tools found in `READ_ONLY/`  
**Author:** Pi coding agent

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [What Is Hash Line Editing?](#what-is-hash-line-editing)
3. [Important Distinction: pi-mono vs oh-my-pi](#important-distinction-pi-mono-vs-oh-my-pi)
4. [oh-my-pi — Hashline System (Fork)](#1-oh-my-pi--hashline-system-fork)
5. [Pi (Original, pi-mono) — Old-Text String Replacement](#2-pi-original-pi-mono--old-text-string-replacement)
6. [Dirac — Stateful Word Anchors](#3-dirac--stateful-word-anchors)
7. [Gemini CLI — Multi-Strategy String Replacement](#4-gemini-cli--multi-strategy-string-replacement)
8. [OpenCode / KiloCode — String Edit + Unified Diff Patch](#5-opencode--kilocode--string-edit--unified-diff-patch)
9. [Codex (OpenAI) — Apply Patch (Rust)](#6-codex-openai--apply-patch-rust)
10. [Zed — No Hash Anchoring](#7-zed--no-hash-anchoring)
11. [Cross-Cutting Comparison Table](#cross-cutting-comparison-table)
12. [Key Design Decisions](#key-design-decisions)
13. [Token Efficiency Analysis](#token-efficiency-analysis)
14. [Failure Mode Comparison](#failure-mode-comparison)
15. [Recommendations](#recommendations)

---

## Executive Summary

This report examines how seven different agent CLI tools handle the fundamental problem of **precisely targeting file edits** in a way that survives context shifts (lines added/removed above the target). The tools fall into three broad architectural families:

| Family | Tools | Approach |
|--------|-------|----------|
| **Content-hash anchors** | oh-my-pi (fork), Dirac | Each line gets a content-derived token. Edits reference `{line}{hash}` or `{wordAnchor}`. |
| **String matching** | Pi (original), Gemini CLI, OpenCode (`edit` tool) | The model provides `oldText`/`oldString` + `newText`/`newString`. The tool finds and replaces verbatim text with fallback strategies. |
| **Unified diff patches** | Codex, OpenCode (`apply_patch` tool) | The model emits a structured patch with context lines (`@@` hunks). The tool parses and applies changes line-by-line. |

**Critical distinction:** The original Pi (repository `pi-mono`) and the fork `oh-my-pi` have diverged significantly. The original Pi uses a conventional `oldText`→`newText` string replacement approach. The fork `oh-my-pi` invented the sophisticated **hashline** system with BPE-optimized bigram anchors, a formal grammar, and a compact editing DSL. These are documented separately below.

---

## What Is Hash Line Editing?

AI coding agents edit files by describing the change to the tool (e.g., "replace lines A through B with this new content"). Traditional line-number-based editing is fragile because:

1. Inserting or deleting a single line anywhere above the target shifts all subsequent line numbers
2. The model must accurately count lines across large files
3. Two edits to the same file in one turn can collide if line numbers aren't updated

**Hash line editing** solves this by assigning each line a stable identifier derived from the line's content (and optionally position). The model copies the identifier verbatim from the read output:

```
Read output:    42sr|    return a + b;
                         ^^^^
                       line hash

Edit command:   = 42sr
                ~    return a + b;
```

The hash is typically:
- **Deterministic** (oh-my-pi) — recomputed from content, no state needed
- **Stateful** (Dirac) — assigned once, persisted across turns via diff reconciliation

When the edit is applied, the tool:
1. Locates the line by its current hash (even if the line number changed slightly)
2. Validates that the content hasn't changed since the last read (hash mismatch → error)
3. Applies the edit

This allows the model to make multiple edits to the same file in one turn without race conditions, and to retry failed edits using updated read output.

---

## Important Distinction: pi-mono vs oh-my-pi

There are **two separate projects** in the READ_ONLY folder:

| Project | Repository | Relationship | Editing System |
|---------|-----------|-------------|----------------|
| **Pi (original)** | `pi-mono/` | The original `@earendil-works/pi-coding-agent` | String replacement via `oldText`/`newText` with fuzzy matching |
| **oh-my-pi** | `oh-my-pi/` | A long-running fork (`@oh-my-pi/pi-coding-agent`) | **Hashline system** with BPE-optimized anchors, Lark grammar, editing DSL |

The original Pi (pi-mono) does **not** use hash line editing. It uses a conventional `edit` tool that accepts `{ path, edits: [{ oldText, newText }] }` — the same `oldText`/`newText` pattern that Pi itself (this agent) uses. The hashline system was developed in the oh-my-pi fork.

Both projects share the same `packages/coding-agent/` package structure but have diverged significantly in their editing implementations.

---

## 1. oh-my-pi — Hashline System (Fork)

**Source:** `READ_ONLY/oh-my-pi/packages/coding-agent/src/edit/`

### Architecture

oh-my-pi's hashline system has four layers:

```
line-hash.ts          Core hash computation + bigram table
modes/hashline.lark   Formal grammar (Lark)
modes/hashline.ts     Parser, validator, applicator, streaming
prompts/tools/hashline.md  Model-facing prompt
```

### Hash Algorithm

Uses **xxHash32** over the line content, modulo 647, mapping to a curated table of 647 two-letter combinations that are each exactly one token in cl100k / o200k BPE vocabularies.

```typescript
computeLineHash(idx: number, line: string): string
```

**Special cases:**
- **Brace-only lines** (whitespace + `{}` only): Get an ordinal suffix (`1st`, `42nd`, `100th`, `3rd`) so the `LINE+ID` pair merges into one ordinal token
- **Punctuation-only lines with no letters/digits:** Hash incorporates the line index as seed to give adjacent identical lines different hashes
- **Lines with significant content:** Hash is independent of line number (stable across small shifts)

### Bigram Table

The `HL_BIGRAMS` array contains 647 entries (all common 2-letter lowercase combos). 29 rare combinations (q/x/z-heavy) are excluded because they don't merge into single tokens. **Order is declared stable forever** — changing it would invalidate every saved `LINE+ID` reference in transcripts.

### Display Format

```
LINE+HASH|CONTENT

Examples:
42sr|function hi() {
3ab|}
100th|}
```

- Separator `|` (constant, `HL_BODY_SEP`): between anchor and content
- Edit payload separator `~` (`HL_EDIT_SEP`, configurable via `PI_HL_SEP` env var): `~TEXT` for inserted/replacement lines

The edit separator was chosen empirically from 8 candidates × 3 models. `~` won because:
- Highest edit success rate (94.9%) of any tested separator
- Lowest patch-failure rate (5.6%) — model rarely emits a malformed payload
- Lowest token cost alongside `>`
- No line-leading role in any mainstream language, markdown, diff, regex, or shell

### Editing DSL

The grammar (`hashline.lark`) defines a compact line-anchored wire format:

```
section:      file_header line_op*
file_header:  "@" PATH
line_op:      inline_before_op | inline_after_op | insert_before_op |
              insert_after_op | replace_op | delete_op

inline_before_op:  "<" LID SEP line_text?     # Prefix to a line
inline_after_op:   "+" LID SEP line_text?     # Suffix to a line
insert_before_op:  "<" insert_target          # Insert before anchor/BOF
insert_after_op:   "+" insert_target          # Insert after anchor/EOF
replace_op:        "=" range                  # Replace range A..B
delete_op:         "-" range                  # Delete range A..B
```

**Operations:**

| Op | Syntax | Meaning |
|----|--------|---------|
| `+` | `+ ANCHOR` then `~TEXT` | Insert `TEXT` after ANCHOR (or EOF) |
| `<` | `< ANCHOR` then `~TEXT` | Insert `TEXT` before ANCHOR (or BOF) |
| `-` | `- A..B` | Delete inclusive range `A` through `B` |
| `=` | `= A..B` then `~TEXT` | Replace range with payload text |
| Inline `<` | `< 42sr~prefix` | Prepend `prefix` to line `42sr` |
| Inline `+` | `+ 42sr~suffix` | Append `suffix` to line `42sr` |

### Anchor Validation & Rebase

On edit application, every anchor is validated:

```typescript
function validateHashlineAnchors(edits, fileLines, warnings): HashMismatch[]
```

1. Compute actual hash of each referenced line
2. If hash doesn't match, try **auto-rebase** within ±5 lines
3. If exactly one match found in the window, rebase silently
4. If zero or multiple matches, report `HashlineMismatchError`

The mismatch error returns updated hashline-prefixed file content so the model can immediately retry with correct anchors.

### Streaming

`streamHashLinesFromUtf8` converts any UTF-8 byte stream into hashline-prefixed chunks, capped at 200 lines or 64 KiB per chunk. Useful for piping file reads directly into the model context.

### Context Duplication Absorption

When the model emits a replacement that includes lines already present outside the range, the system auto-drops them:
- **Prefix/suffix blocks** (≥2 matching lines): assumed context echo, removed
- **Structural closing boundaries** (`}`, `});`, etc.): single-line absorb to prevent syntax errors from off-by-one ranges
- Uses delimiter balance analysis to verify the absorb is safe

### Key Files

| File | Purpose |
|------|---------|
| `src/edit/line-hash.ts` | Core hash computation, bigram table, format helpers |
| `src/edit/modes/hashline.ts` | Full implementation: parser, validator, applicator, diff preview, streaming |
| `src/edit/modes/hashline.lark` | Formal grammar |
| `src/prompts/tools/hashline.md` | Model-facing prompt |
| `test/core/hashline.test.ts` | Tests |

---

## 2. Pi (Original, pi-mono) — Old-Text String Replacement

**Source:** `READ_ONLY/pi-mono/packages/coding-agent/src/core/tools/edit.ts`

### Architecture

The original Pi (pi-mono) uses a **conventional `oldText`→`newText` string replacement** approach, similar to Gemini CLI's design. It does **not** implement hash line editing.

### Tool Interface

```typescript
interface EditToolInput {
  path: string;               // Path to the file
  edits: Array<{              // One or more targeted replacements
    oldText: string;           // Exact text to find (must be unique)
    newText: string;           // Replacement text
  }>;
}
```

This is the same `{ path, edits: [{ oldText, newText }] }` pattern that Pi (this agent) itself uses. Each edit targets a unique `oldText` string in the file, and edits must not overlap.

### Legacy Support

The tool also accepts a legacy flat format (`oldText` and `newText` at the top level alongside `edits`) for backward compatibility with models that emit parameters this way. The legacy fields are merged into the `edits` array.

### Fuzzy Matching

Pi's edit tool includes a `normalizeForFuzzyMatch()` function that progressively normalizes text for more resilient matching:

```typescript
function normalizeForFuzzyMatch(text: string): string {
  return (
    text
      .normalize("NFKC")
      .split("\n").map(line => line.trimEnd()).join("\n")
      .replace(/[\u2018\u2019\u201A\u201B]/g, "'")      // Smart quotes → '
      .replace(/[\u201C\u201D\u201E\u201F]/g, '"')      // Smart double quotes → "
      .replace(/[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]/g, "-") // Dashes → -
      .replace(/[\u00A0\u2002-\u200A\u202F\u205F\u3000]/g, " ")      // Unicode spaces → space
  );
}
```

This normalization handles:
- Unicode punctuation normalization (NFKC)
- Trailing whitespace stripping per line
- Smart quotes → ASCII quotes
- Various Unicode dashes/hyphens → ASCII hyphen
- Non-breaking spaces and other Unicode spaces → regular space

### Diff Support

- Uses `diff` npm package to generate unified diffs (`Diff.createPatch`, `diffLines`)
- `computeEditsDiff()` for computing the impact of a set of edits
- `generateDiffString()` for formatting diffs for display

### Key Distinction from oh-my-pi

| Aspect | Pi (original, pi-mono) | oh-my-pi (fork) |
|--------|----------------------|-----------------|
| **Edit format** | `{ path, edits: [{ oldText, newText }] }` | `@path\n+ ANCHOR\n~TEXT` |
| **Hash anchors** | None | BPE-optimized 2-char bigrams |
| **Matching** | Exact + fuzzy Unicode normalization | Content-hash anchors with ±5 rebase |
| **Multiple edits** | Array of `{oldText, newText}` | Multiple op lines per `@path` section |
| **Grammar** | None | Lark grammar (`hashline.lark`) |

### Key Files

| File | Purpose |
|------|---------|
| `src/core/tools/edit.ts` | Edit tool implementation with string replacement |
| `src/core/tools/edit-diff.ts` | Diff computation, normalization, fuzzy matching utilities |
| `src/core/tools/file-mutation-queue.ts` | Write queue for sequenced file mutations |

---

## 3. Dirac — Stateful Word Anchors

**Source:** `READ_ONLY/dirac/src/utils/AnchorStateManager.ts`, `src/shared/utils/line-hashing.ts`

### Architecture

Dirac's approach is fundamentally different from both Pi variants: anchors are **stateful, human-readable words** that persist across editing turns using Myers diff reconciliation.

```
AnchorStateManager.ts   Stateful anchor storage + diff reconciliation
line-hashing.ts         Core utilities (delimiter, format, strip, split)
tools/handlers/edit-file/   EditExecutor.ts, BatchProcessor.ts, etc.
```

### Anchor Format

```
WORD§CONTENT

Examples:
Apple§    def process(data):
Brave§    total = 0
Cider§    for item in items:
```

Delimiter: `§` (section sign, `ANCHOR_DELIMITER`)

### Hash Algorithm

Uses **FNV-1a 32-bit** (non-cryptographic, very fast):

```typescript
function contentHash(content: string): string {
    let h = 2166136261
    for (let i = 0; i < content.length; i++) {
        h = Math.imul(h ^ content.charCodeAt(i), 16777619)
    }
    return (h >>> 0).toString(16).padStart(8, "0")
}
```

Hashes are stored internally as `Uint32Array` and used for diff comparison, never exposed to the model. The model sees **word anchors** instead.

### Anchor Word Dictionary

Words are read from a `.hash_anchors` dictionary file. Each anchor is a random concatenation of two dictionary words (e.g., `Apple` + `Brave` → `AppleBrave`). For larger files, three-word combinations are used as fallback when the two-word pool is exhausted.

- The pool starts with a shuffled copy of all single words from the dictionary
- Generates 10,000 two-word combinations on demand
- Avoids collisions with already-used words
- Words are guaranteed to start with a capital letter

### State Management (AnchorStateManager)

This is the key innovation. The manager maintains a per-task, per-file state map:

```
Map<taskId, Map<absolutePath, TrackedDocument>>
```

Each `TrackedDocument` contains:
- `hashes: Uint32Array` — FNV-1a hashes of each line
- `anchors: string[]` — assigned word anchors
- `usedWords: Set<string>` — all assigned words (for collision avoidance)
- `availablePool: string[]` — pre-generated candidate words

#### Reconciliation Flow

When a file is re-read after edits:

1. Compute FNV-1a hashes of current lines
2. Run **Myers diff** between old hash array and new hash array (using `diff.diffArrays`)
3. For unchanged regions: copy old anchors verbatim
4. For added lines: assign new random words
5. For deleted lines: drop their anchors
6. Update cache with new anchors + hashes

This means **a line that didn't change keeps its exact same word anchor** across multiple turns, even if other lines above/below were added or deleted.

#### LRU Eviction

- Max 50,000 lines per file
- Max 1,024 files per task
- Max 50 tasks cached

### Editing API

Dirac exposes a single `edit_file` tool with three edit types, supporting batched multi-file edits:

```json
{
  "files": [
    {
      "path": "src/calculator.py",
      "edits": [
        {
          "edit_type": "insert_before",
          "anchor": "Apple§    def process(data):",
          "text": "from typing import List"
        },
        {
          "edit_type": "replace",
          "anchor": "Brave§    total = 0",
          "end_anchor": "Eagle§            total += item.price",
          "text": "    total = sum(item.price for item in items if item.price > 0)"
        },
        {
          "edit_type": "insert_after",
          "anchor": "Snake§}",
          "text": "\nexport function isAnonymous(user: User): boolean {\n  return !user.name;\n}"
        }
      ]
    }
  ]
}
```

### Validation

The `EditExecutor.resolveAnchor()` method performs four checks:

1. **Format validation:** Anchor must start with capital letter, letters only
2. **Existence check:** Word must be in current file's tracked anchors
3. **No newlines:** Content part must not contain `\n` or `\r`
4. **Content match:** Provided content must match actual file content exactly

### Key Properties

- **Stateful between turns** — same line keeps its anchor across edits
- **Human-readable words** — easier for LLMs to copy correctly than `42sr`
- **Deterministic hashing for diff, non-deterministic word assignment**
- **Batched multi-file editing** — one tool call, multiple files
- **LRU caches** at task, file, and line levels
- **No grammar or DSL** — uses structured JSON parameters

### Key Files

| File | Purpose |
|------|---------|
| `src/utils/AnchorStateManager.ts` | Stateful anchor storage, Myers diff reconciliation, word generation |
| `src/utils/line-hashing.ts` | Format line with hash, split anchor, hash computation |
| `src/shared/utils/line-hashing.ts` | Shared delimiter constant (`§`), strip hashes, extract ID |
| `src/core/task/tools/handlers/edit-file/EditExecutor.ts` | Resolve anchors, apply edits |
| `src/core/prompts/system-prompt/sections/editing-files.ts` | Model-facing instructions |

---

## 4. Gemini CLI — Multi-Strategy String Replacement

**Source:** `READ_ONLY/gemini-cli/packages/core/src/tools/edit.ts`

### Architecture

Gemini CLI takes a simple approach: **no hash anchors**. The model provides `old_string` and `new_string`, and the tool tries four matching strategies in order.

```
calculateReplacement(config, context) → ReplacementResult
  ├─ calculateExactReplacement()      # Literal string match
  ├─ calculateFlexibleReplacement()   # Trimmed-line matching
  ├─ calculateRegexReplacement()      # Tokenized regex matching
  └─ calculateFuzzyReplacement()      # Levenshtein distance
```

### Tool Parameters

```typescript
interface EditToolParams {
  file_path: string;
  old_string: string;
  new_string: string;
  allow_multiple?: boolean;     // Replace all occurrences
  instruction?: string;         // For LLM self-correction
  modified_by_user?: boolean;
  ai_proposed_content?: string;
}
```

### Matching Strategies

#### 1. Exact (`calculateExactReplacement`)
- Normalizes line endings (CRLF → LF)
- Splits by `old_string` verbatim
- Counts occurrences
- Fails if `!allow_multiple && occurrences > 1`

#### 2. Flexible (`calculateFlexibleReplacement`)
- Splits both old_string and file into lines
- Trims whitespace on each search line
- Matches by trimmed line content
- Preserves indentation from the first matched line
- Applies the same indentation to the replacement block

#### 3. Regex (`calculateRegexReplacement`)
- Splits old_string into tokens by common delimiters: `( ) : [ ] { } > < =`
- Joins tokens with `\s*` (flexible whitespace)
- Captures leading indentation
- Creates `/^([ \t]*)TOKEN_PATTERN/gm` regex

#### 4. Fuzzy (`calculateFuzzyReplacement`)
- Only for strings ≥ 10 characters
- **Levenshtein distance** on full text
- **Tiered scoring:** weighted score = `d_norm + (d_raw - d_norm) * 0.1`
- Threshold: 10% weighted difference
- Length heuristic optimization to skip impossible matches
- O(N * L²) complexity cap of 4e8 operations
- Selects best non-overlapping matches

### Self-Correction

If all strategies fail:
1. Calls `FixLLMEditWithInstruction` — passes error message + file content to an LLM
2. The correction LLM returns corrected `old_string` and `new_string`
3. Retries `calculateReplacement` with corrected values
4. If correction fails too, reports original error

### Line Ending Handling

- Detects original line ending (CRLF vs LF) via `detectLineEnding`
- Normalizes to LF for matching
- Restores original line endings on write
- Preserves trailing newline behavior

### Diff Generation

After successful edit:
- Generates unified diff via `Diff.createPatch()`
- Computes `DiffStat` (model_added_lines, model_removed_lines)
- Returns diff context snippet (5 lines of context) so the model can skip a verification read

### Key Files

| File | Purpose |
|------|---------|
| `src/tools/edit.ts` | Full implementation: strategies, self-correction, confirmation, execution |
| `src/tools/diff-utils.ts` | Diff context snippets |
| `src/tools/diffOptions.ts` | Diff configuration |
| `src/utils/textUtils.ts` | `safeLiteralReplace`, `detectLineEnding` |

---

## 5. OpenCode / KiloCode — String Edit + Unified Diff Patch

**Source:** `READ_ONLY/opencode/packages/opencode/src/tool/`

### Architecture

OpenCode provides **two separate editing tools** with different approaches.

### A) The `edit` Tool (Content-Based)

**Parameters:**
```typescript
interface EditParams {
  filePath: string;
  oldString: string;
  newString: string;
  replaceAll?: boolean;
}
```

- Implements the same multi-strategy matching approach as Gemini CLI
- Sources strategies from Cline and Gemini CLI reference implementations
- Uses `diff` npm package for diff generation
- Supports file creation (empty `oldString` + file doesn't exist)
- LSP formatting after editing
- File watching events (`File.Event.Edited`)
- Per-file semaphore locking for concurrent safety

### B) The `apply_patch` Tool (Unified Diff)

**Parameters:**
```typescript
interface ApplyPatchParams {
  patchText: string;
}
```

Parses a structured patch format inherited from Codex's Rust implementation:

```
*** Begin Patch
*** Add File: path/to/file
+content line 1
+content line 2

*** Update File: path/to/file2
@@
 context line
-old line
+new line
@@
 another context
-old line 2
+new line 2

*** Delete File: path/to/file3
*** End Patch
```

### Patch Matching

Uses a **four-pass matching** approach in `seekSequence()`:

1. **Exact** — byte-for-byte comparison
2. **Rstrip** — trim trailing whitespace before comparison
3. **Trim** — trim both ends before comparison
4. **Unicode normalized** — normalize Unicode punctuation to ASCII (smart quotes → straight quotes, dashes → hyphens)

### Patch Types

| Hunk Type | Syntax |
|-----------|--------|
| Add | `*** Add File: path` + `+content` lines |
| Update | `*** Update File: path` + `@@` sections with `-`/`+`/` ` lines |
| Delete | `*** Delete File: path` (no body) |
| Move | `*** Update File: path` + `*** Move to: dest` + `@@` section |

### Additional Features

- **Heredoc extraction:** Detects `apply_patch <<'PATCH'...PATCH` in bash scripts
- **BOM handling:** Reads/writes files with BOM awareness
- **LSP formatting:** Re-formats files after edit if applicable
- **Multi-file support:** One patch can modify/add/delete multiple files
- **Change context seeking:** Matches context lines before applying changes

### Key Files

| File | Purpose |
|------|---------|
| `src/tool/edit.ts` | String-based edit tool (old/new string) |
| `src/tool/apply_patch.ts` | Patch-based edit tool |
| `src/patch/index.ts` | Patch parser, `deriveNewContentsFromChunks`, matching engine |

---

## 6. Codex (OpenAI) — Apply Patch (Rust)

**Source:** `READ_ONLY/codex/codex-rs/apply-patch/`

### Architecture

Codex's system is the **origin** of the unified diff patch format adopted by OpenCode. It's implemented in Rust with sandbox-aware filesystem operations.

```
lib.rs              Patch application, hunk processing, diff generation
parser.rs           Patch parsing (Hunk, AddFile, DeleteFile, UpdateFile)
invocation.rs       Shell heredoc detection + Tree-sitter Bash parsing
seek_sequence.rs    Line matching algorithm
streaming_parser.rs Streaming variant for incremental parsing
```

### Patch Format

Same as OpenCode's format, with the addition of:
- `*** End of File` marker for EOF-aware insertions
- `*** Move to:` directive for rename operations

### Matching Algorithm (`seek_sequence`)

Uses the same four-pass approach as OpenCode, implemented in Rust:

1. **Exact match**
2. **Right-strip** (trim trailing whitespace)
3. **Trim** (both ends)
4. **Unicode normalize** (dashes, quotes, ellipsis, non-breaking space)

The matching is done on `Vec<String>` (line-split file content) using a sliding window.

### Delta Tracking

Codex tracks `AppliedPatchDelta` — a record of every change actually committed to the filesystem:

```rust
struct AppliedPatchDelta {
    changes: Vec<AppliedPatchChange>,
    exact: bool,  // false if any operation had uncertainty
}

enum AppliedPatchFileChange {
    Add { content, overwritten_content? },
    Delete { content },
    Update { move_path?, old_content, overwritten_move_content?, new_content },
}
```

Even if a patch fails partway through, the delta is preserved so the caller knows what was already applied.

### Heredoc Detection (Tree-Sitter)

Uses **Tree-sitter Bash grammar** with a strict query to detect `apply_patch <<'EOF'...EOF` invocations from `bash -lc` scripts. Supports:
- Direct `apply_patch <<'PATCH'...PATCH`
- `cd <path> && apply_patch <<'PATCH'...PATCH`
- PowerShell and cmd variants

The query is anchored to ensure the heredoc call is the **only** top-level statement (no `echo` prefixes or `&& echo done` suffixes).

### Sandbox Integration

All filesystem operations go through the `ExecutorFileSystem` trait, which supports sandboxed execution (sandbox contexts with path allow/deny lists, safe directory creation, etc.).

### Key Files

| File | Purpose |
|------|---------|
| `apply-patch/src/lib.rs` | Core patch application, hunk processing, diff generation |
| `apply-patch/src/parser.rs` | Patch string → `Hunk[]` parsing |
| `apply-patch/src/invocation.rs` | Tree-sitter heredoc extraction from shell commands |
| `apply-patch/src/seek_sequence.rs` | Multi-pass line matching algorithm |
| `apply-patch/src/streaming_parser.rs` | Streaming (incremental) parser |
| `core/src/apply_patch.rs` | High-level integration with Codex core |

---

## 7. Zed — No Hash Anchoring

**Source:** `READ_ONLY/zed/AGENTS.md`, `READ_ONLY/zed/docs/AGENTS.md`

Zed does **not** implement a hash-anchored or content-based editing system. Its agent integration works through:
- **Keybinding/action macros** (`{#kb agent::ToggleFocus}`, `{#action agent::OpenSettings}`)
- **Documentation preprocessor** that expands these macros into formatted keybinding references
- **Standard editor mechanisms** (LSP, text buffers)

Zed's AGENTS.md files focus on:
- mdBook documentation automation
- Prettier formatting requirements
- Preprocessor syntax for keybindings and actions

There is no custom hash system, no `edit` tool with string matching, and no patch format. Edits are handled through the editor's native text manipulation or the assistant panel.

---

## Cross-Cutting Comparison Table

| Feature | **oh-my-pi** (Hashline) | **Pi** (original) | **Dirac** | **Gemini CLI** | **OpenCode** (edit) | **OpenCode** (apply_patch) | **Codex** |
|---------|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **Anchor type** | 2-char BPE bigram | None | Random word pair | None | None | Context lines | Context lines |
| **Anchor format** | `42sr\|text` | N/A | `Apple§text` | N/A | N/A | `@@` hunk | `@@` hunk |
| **Hash algorithm** | xxHash32 mod 647 | N/A | FNV-1a 32-bit | N/A | N/A | N/A | N/A |
| **Stateful?** | No | No | Yes (Myers diff) | No | No | No | No |
| **BPE token cost** | 1 token/anchor | N/A | ~1-3 tokens/anchor | N/A | N/A | Variable | Variable |
| **Multiple files/turn** | Yes (`@path`) | No | Yes (batched) | No | No | Yes | Yes |
| **File moves** | No | No | No | No | No | Yes | Yes |
| **Match strategies** | Rebase ±5 | Exact + Unicode | Myers diff | Exact→Flex→Regex→Fuzzy | Exact (only) | 4-pass line match | 4-pass + Unicode |
| **Self-correction** | Structured error | No | Structured error | LLM correction | No | No | No |
| **LSP integration** | Yes | No | No | No | Yes | Yes | No |
| **Sandbox support** | No | No | No | No | No | No | Yes (Rust) |
| **Implementation** | TypeScript | TypeScript | TypeScript | TypeScript | TypeScript (Effect) | TypeScript (Effect) | Rust |
| **Streaming read** | Yes | No | No | No | No | No | Streaming parser |
| **Edit DSL/Grammar** | Lark grammar | N/A | JSON params | N/A | N/A | Patch format | Patch format |

---

## Key Design Decisions

### 1. Deterministic vs. Stateful Anchors

**oh-my-pi** uses **deterministic anchors**: the hash is purely a function of line content (and optionally line number for punctuation-only lines). No state needed. The model can copy-paste anchors from read output and trust they'll work. Rebasing is limited to a ±5 line window.

**Dirac** uses **stateful anchors**: words are assigned randomly and persisted across turns via Myers diff. A line that doesn't change keeps its exact word anchor even after insertions above. This is more resilient but requires the tool to maintain state between turns.

**Pi (original)** and **Gemini CLI** use **no anchors** — they rely on string matching. This is simpler but more fragile.

**Trade-off:** Deterministic is simpler (no state, no cache) but less resilient to context shifts. Stateful is more resilient but adds complexity (LRU caches, diff reconciliation, word pool management). String matching requires no anchor infrastructure but can't handle regenerated or reformatted code.

### 2. Anchor Token Cost

**oh-my-pi's approach:** Every anchor is exactly 1 BPE token (line number digits merge with bigram). For brace-only lines, the anchor is an ordinal suffix (`1st`, `42nd`) that also merges into one token.

**Dirac's approach:** Word anchors are ~6-12 characters. A two-word anchor like `AppleBrave` is ~4 BPE tokens. This is more expensive per line but human-readable.

**Impact:** oh-my-pi saves ~3-4 tokens per line of output compared to Dirac. Over a 200-line file read, this is 600-800 tokens saved. For long sessions with many file reads, this adds up significantly.

### 3. Edit DSL Expressiveness

| Capability | oh-my-pi | Pi (orig) | Dirac | Gemini | OpenCode | Codex |
|------------|:--------:|:---------:|:-----:|:------:|:--------:|:-----:|
| Insert before anchor | ✓ | ~ | ✓ | ~ | ~ | ~ |
| Insert after anchor | ✓ | ~ | ✓ | ~ | ~ | ~ |
| Delete single line | ✓ | ~ | ✓ | ~ | ~ | ~ |
| Delete range | ✓ | ~ | ✓ | ~ | ~ | ~ |
| Replace range | ✓ | ~ | ✓ | ~ | ✓ | ✓ |
| Inline prefix/suffix | ✓ | No | No | No | No | No |
| Multi-file in one call | ✓ | No | ✓ | No | No | ✓ |
| File move | No | No | No | No | No | ✓ |

*Note: `~` means achievable via the `oldText`/`oldString` pattern (replace the text before and after the desired location), but not as a first-class operation.*

### 4. Matching Robustness

| System | Whitespace tolerance | Unicode tolerance | Context shift tolerance |
|--------|:--------------------:|:-----------------:|:-----------------------:|
| **oh-my-pi** | None (strict) | None | ±5 line rebase |
| **Pi (original)** | No (exact) | Yes (NFKC + Unicode normalization) | None (string match) |
| **Dirac** | None (content exact) | None | Full (diff-based) |
| **Gemini** | Flexible + Regex + Fuzzy | None | None (string match) |
| **OpenCode (edit)** | None (strict) | None | None |
| **OpenCode (patch)** | Rstrip + Trim | Yes (Unicode norm) | Context lines |
| **Codex** | Rstrip + Trim | Yes (Unicode norm) | Context lines |

---

## Token Efficiency Analysis

Estimated token cost per 1000-line file read, based on BPE tokenization:

| System | Anchors/line | Tokens/line | Total tokens (1000 lines) | Notes |
|--------|:-----------:|:-----------:|:-------------------------:|-------|
| **Raw text** (no anchors) | 0 | ~1.3 | ~1,300 | Baseline |
| **oh-my-hi (Hashline)** | 1 | ~2.3 | ~2,300 | 1 token anchor + ~1.3 token content |
| **Pi (original)** | 0 | ~1.3 | ~1,300 | No anchors |
| **Dirac** | 1 | ~4.3 | ~4,300 | ~3 token anchor + ~1.3 token content |
| **Gemini CLI** | 0 | ~1.3 | ~1,300 | No anchors |
| **OpenCode patch** | N/A | ~2.5 | ~2,500 | Context + diff markers |
| **Codex patch** | N/A | ~2.5 | ~2,500 | Context + diff markers |

**oh-my-pi** is the most token-efficient anchoring system because:
- 2-char bigrams merge with the line number into one BPE token
- Brace-only lines use ordinal suffixes that produce 1-token anchors
- The anchor+content separator `|` is often part of adjacent tokens

**Dirac** is the least token-efficient because word anchors (6-12 chars) cannot merge with surrounding content.

**Pi (original)** and **Gemini CLI** pay zero anchor overhead but incur retry costs when string matching fails.

---

## Failure Mode Comparison

### What happens when the editing system fails?

| System | Failure mode | Error recovery |
|--------|-------------|----------------|
| **oh-my-pi** | Hash mismatch → `HashlineMismatchError` | Tool returns updated file content with corrected hashes. Model retries with new anchors. |
| **Pi (original)** | oldText not found (0 occurrences) or ambiguous (>1) | Error message. Model must read file and retry. |
| **Dirac** | Anchor not found or content mismatch | Tool returns diagnostic ("anchor X not found" or "expected Y, got Z"). Model retries with updated read output. |
| **Gemini CLI** | 0 occurrences or multiple occurrences | `getErrorReplaceResult`. Optionally triggers LLM self-correction to fix search string. |
| **OpenCode (edit)** | 0 occurrences | Throws error. No self-correction. |
| **OpenCode (patch)** | Context line not found | Throws error with "Failed to find context". No self-correction. |
| **Codex** | Context line not found | `ComputeReplacements` error. No self-correction. |

oh-my-pi and Dirac give the most actionable error feedback (they tell the model what the correct anchors/values are). Gemini CLI has the most sophisticated retry logic (LLM-based parameter correction). Pi (original), OpenCode, and Codex have the simplest failure handling (error and let the model figure it out).

---

## Recommendations

### If building a new agent CLI:

1. **For maximum token efficiency:** Adopt oh-my-pi's approach — xxHash32 + BPE-optimized bigram table. The 1-token-per-anchor cost is hard to beat over thousands of lines of read output.

2. **For maximum resilience:** Adopt Dirac's stateful approach — Myers diff reconciliation keeps anchors stable across edits. The extra token cost is offset by fewer retries.

3. **For simplicity (if hash anchors are overkill):** Use Pi (original) or Gemini CLI's approach — no anchors, just string matching with Unicode normalization fallback. Less infrastructure to maintain, but more fragile with generated/formatted code.

4. **For multi-file operations:** Support a unified patch format like Codex/OpenCode's `*** Begin Patch` format. This enables multi-file edits, file moves, and batch operations in a single tool call.

5. **For streaming large files:** Implement oh-my-pi's `streamHashLinesFromUtf8` to avoid memory issues with large files.

### Best practices across all systems:

- **Always validate edits before writing** — hash validation (oh-my-pi, Dirac) or occurrence counting (Pi, Gemini) catches stale references
- **Provide structured error feedback** — tell the model what the correct anchors/values are, not just "failed"
- **Support batched operations** — multiple edits per turn saves API calls and latency
- **Auto-absorb context duplication** — models frequently echo surrounding code; absorb it silently with safety checks
- **Handle line endings** — normalize to LF internally, restore original on write
- **Distinguish forks clearly** — as the pi-mono vs oh-my-pi split shows, forked projects can diverge significantly in their editing infrastructure

---

## Source References

All source material is in `READ_ONLY/`:

| Tool | Path |
|------|------|
| **Pi (original)** | `pi-mono/packages/coding-agent/src/core/tools/edit.ts`, `edit-diff.ts` |
| **oh-my-pi** | `oh-my-pi/packages/coding-agent/src/edit/line-hash.ts`, `modes/hashline.ts`, `modes/hashline.lark` |
| **Dirac** | `dirac/src/utils/AnchorStateManager.ts`, `src/shared/utils/line-hashing.ts`, `src/utils/line-hashing.ts` |
| **Gemini CLI** | `gemini-cli/packages/core/src/tools/edit.ts` |
| **OpenCode** | `opencode/packages/opencode/src/tool/edit.ts`, `tool/apply_patch.ts`, `patch/index.ts` |
| **Codex** | `codex/codex-rs/apply-patch/src/lib.rs`, `parser.rs`, `invocation.rs` |
| **Zed** | `zed/AGENTS.md`, `zed/docs/AGENTS.md` |
