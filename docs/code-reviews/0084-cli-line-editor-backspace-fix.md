# Code Review 0084: CLI LineEditor Backspace Fix

**Status:** Fixed  
**Date:** 2026-05-10  
**Build:** 0 warnings, 0 errors  
**Tests:** 417 passing, 0 failed

---

## Issue

Interactive CLI backspace did not work correctly in the input line.

Root cause was in `Omicron.CLI/LineEditor.cs`: after `LineEditorEngine` processed Backspace, the renderer used the post-edit cursor/buffer and attempted to inspect `_buf[_col]`.

For normal end-of-line backspace, the engine changes state from:

```text
buffer length = N, cursor = N
```

to:

```text
buffer length = N - 1, cursor = N - 1
```

Then the renderer could access `_buf[_col]` where `_col == _buf.Length`, which is out of range.

## Fix

`LineEditor.ReadLine` now captures the previous buffer/cursor before processing each key and passes that to the renderer.

Renderer changes:

- Backspace rendering now checks `previousCursor > 0`.
- Deleted character is read from `previousBuffer[previousCursor - 1]`.
- End-of-line backspace clears the deleted character without indexing past the new buffer.
- Middle-of-line backspace/delete redraws the shifted tail and clears one extra stale character.
- Left/right arrow rendering also uses previous cursor/buffer so movement to/from boundaries renders correctly.

## Verification

```text
dotnet build
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet test --no-build
Passed: 417, Failed: 0, Skipped: 0
```
