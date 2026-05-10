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

Add or extend a read tool that can return line hashes:

```text
read_file_hashlines
```

or add an option to `read_path` later:

```json
{ "path": "src/Foo.cs", "include_hashes": true }
```

Line hash should be stable and short, e.g. first 8-12 hex chars of SHA-256 over normalized line bytes.

## Edit Tool Input

```json
{
  "path": "src/Foo.cs",
  "edits": [
    {
      "start_line": 42,
      "start_hash": "abc12345",
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
   - line exists;
   - start hash matches;
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

- stale hash;
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
- Rejects stale line hash with current nearby context.
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
