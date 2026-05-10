# Implementation Plan 0004: Transaction-Backed Edit Harness v1

Status: Proposed  
Depends on: Plan 3 Phase 4 workspace transactions/diffs, Plan 3.25 transaction manager  
Primary RFCs: RFC 0012, RFC 0006

## Purpose

Introduce the first high-reliability edit tool on top of workspace transactions. The tool should validate preconditions, stage changes, return a diff, and commit only when safe.

## Recommended Tool

Start with hashline/stateless anchors:

```text
edit_file_hashline
```

## Read/Anchor Flow

Add or extend a read tool that can return compact line anchors:

```text
read_file_hashlines
```

or add an option to `read_path` later:

```json
{ "path": "src/Foo.cs", "include_hashes": true }
```

Line anchors should be stable, short, and cheap to compute. Do **not** use SHA-256 for MVP hashlines: cryptographic hashing is unnecessary overhead and 8-12 hex characters are token-inefficient. Based on `docs/reports/hash-line-editing-systems-report.md`, prefer a non-cryptographic 32-bit content hash such as xxHash32 or FNV-1a, mapped to a compact anchor id.

Recommended MVP anchor strategy:

- Compute a 32-bit non-cryptographic hash over normalized line text.
- Normalize line endings; do not include `\r`/`\n` terminators in the line hash.
- Render anchors as `line + shortId`, e.g. `42sr|content`.
- **Do not use digits in the short id.** Digits next to line numbers create ambiguity about where the line number ends and the hash begins (e.g. `42a3|content` — is it line 42, hash `a3`, or line 423, hash something-else?). Stick to a pure alphabetic alphabet.
- Use a stable 2-character lowercase-letter alphabet/table where practical; keep ordering stable forever once shipped.
- If no BPE-optimized table is added in MVP, use a pure-letter base26 or custom 2-letter table derived from the 32-bit hash (e.g. `(hash % 676) → two letters a-z`).
- Treat the line number as a hint and the short id as the stale-content guard.
- Keep `old_text` validation as a second guard against collisions.

Future hardening can adopt the full oh-my-pi-style BPE-optimized bigram table and special-case brace-only/punctuation-only lines.

## Edit Tool Input

```json
{
  "path": "src/Foo.cs",
  "edits": [
    {
      "start_line": 42,
      "start_hash": "sr",
      "old_text": "existing text",
      "new_text": "replacement text"
    }
  ]
}
```

## Behavior

1. Resolve path through workspace VFS.
2. Read current file bytes.
3. Validate file is text.
4. Validate each edit:
   - line exists, or can be uniquely rebased within a small window such as ±5 lines;
   - start hash/short id matches the resolved current line;
   - old text matches current content at target span;
   - edits are non-overlapping.
5. Apply edits to an in-memory buffer.
6. Create workspace transaction.
7. Stage file write through `tx.Files.WriteFileAsync(...)`.
8. Generate diff with `tx.GetDiffAsync()`.
9. Commit if validation passed and tool policy says auto-commit.

Initial MVP may auto-commit after validation. Future UI can present diff and ask for approval.

## Error Responses

Errors should be model-actionable:

- stale hash/anchor;
- old text mismatch;
- overlapping edits;
- binary file;
- path not found;
- containment violation;
- diff omitted/truncated.

Include nearby current context and hashes when stale/mismatch occurs.

## Acceptance Criteria

- Applies one valid edit transactionally.
- Applies multiple non-overlapping edits transactionally.
- Rejects stale line hash/anchor with current nearby context, unless it can be uniquely rebased.
- Rejects old text mismatch.
- Rejects overlapping edits.
- Rejects binary files.
- Rollback/dispose leaves host unchanged on validation failure.
- Successful commit returns unified diff.
- Tests cover success/failure paths.
- Build/test pass with no warnings.

## Non-Goals

- No AST-aware edits.
- No semantic refactoring.
- No multi-file edit batches in v1 unless easy after single-file path is stable.
- No UI approval flow yet.
