# Implementation Plan: Paste Handling & Multi-Line Input

**Date:** 2025-05-11
**Status:** Ready for implementation
**Est. effort:** ~5 hours
**Related audit:** [tui-audit-2025-05-11.md](../reports/tui-audit-2025-05-11.md)

---

## 1. Summary

The TUI input editor currently only supports single-line input. When a user pastes multi-line text, each newline immediately submits a message, and the pasted content is processed character-by-character instead of atomically. This plan covers:

1. **Bracketed paste support** — detect paste envelopes, insert content atomically.
2. **Multi-line input editor** — allow typing or pasting multi-line messages before submitting with `Ctrl+Enter`.

---

## 2. Current State

| Feature | Status |
|---------|--------|
| Bracketed paste mode enabled | ✅ `TuiShell.RunAsync` sends `ESC[?2004h` via `TerminalScope.UseBracketedPaste` |
| Bracketed paste parsing | ❌ `SystemTerminalBackend.TryParseEscapeSequence` does not recognise `ESC[200~` / `ESC[201~` |
| Paste handling | ❌ Multi-line pastes arrive as individual `KeyEvent` characters; newlines submit immediately |
| Multi-line input | ❌ `InputEditorWidget` is hardcoded single-line (`Measure` returns height `1 + popup`) |
| Submit key | `Enter` always submits, even with `Shift`/`Ctrl` |

---

## 3. Architecture Overview

```
┌─────────────────┐     Read()     ┌─────────────────┐
│  Windows console │───────────────▶│  SystemTerminal │
│  input stream    │                │  Backend        │
└─────────────────┘                └─────────────────┘
                                           │
                                           │ Parse
                                           │
                    ┌──────────────────────┼──────────────────────┐
                    │                      │                      │
                    ▼                      ▼                      ▼
            ┌─────────────┐       ┌─────────────┐        ┌─────────────┐
            │  KeyEvent   │       │ PasteEvent  │        │ MouseEvent  │
            └─────────────┘       └─────────────┘        └─────────────┘
                                          │
                                          ▼
                                   ┌─────────────┐
                                   │ AppLayout   │
                                   │ OnTerminal  │
                                   │   Event     │
                                   └─────────────┘
                                          │
                                          ▼
                                   ┌─────────────┐
                                   │ InputEditor │
                                   │   Widget    │
                                   │ (multi-line)│
                                   └─────────────┘
```

---

## 4. Phase A: Bracketed Paste Support

### 4.1 New event type

Add to `Omicron.Core/Rendering/TerminalEvent.cs`:

```csharp
public sealed record PasteEvent(string Text) : TerminalEvent;
```

### 4.2 Parser changes

In `Omicron.Core/Rendering/SystemTerminalBackend.cs`:

- Add state fields:
  ```csharp
  private bool _accumulatingPaste;
  private readonly StringBuilder _pasteBuffer = new();
  ```
- In `TryParseEscapeSequence`, detect:
  - `ESC[200~` → set `_accumulatingPaste = true`, consume, return `true` (no event yet)
  - `ESC[201~` → set `_accumulatingPaste = false`, emit `PasteEvent(_pasteBuffer.ToString())`, clear buffer
- In the main read loop (`ReadEventsImpl`), when `_accumulatingPaste == true`, append every byte verbatim to `_pasteBuffer` instead of parsing as escape sequences or UTF-8 characters
- If the buffer is exhausted while accumulating, preserve the partial paste for the next read (similar to incomplete UTF-8 handling)

### 4.3 Wire through AppLayout

In `Omicron.CLI/Tui/AppLayout.cs`, `OnTerminalEvent`:

```csharp
case PasteEvent pe:
    _input.Insert(pe.Text);
    _shell.RequestRender();
    return true;
```

`InputEditorWidget.Insert` must handle newlines: in multi-line mode (Phase B), split on `\n` and create new lines. Before Phase B is complete, replace newlines with spaces as a temporary fallback.

---

## 5. Phase B: Multi-Line Input Editor

### 5.1 Data model changes

Replace `StringBuilder _buffer` in `InputEditorWidget` with:

```csharp
private readonly List<StringBuilder> _lines = [new StringBuilder()];
private int _cursorLine;   // 0-based line index
private int _cursorColumn; // character index within _lines[_cursorLine]
```

### 5.2 Core operations

| Operation | Behaviour |
|-----------|-----------|
| `Insert(string text)` | Split `text` on `\n` (and `\r\n`). Append first part to current line. For each subsequent part, append new `StringBuilder` to `_lines` and insert remainder. |
| `Backspace()` | If `_cursorColumn > 0`: remove char before column. If `_cursorColumn == 0` and `_cursorLine > 0`: append current line to previous line, remove current line, move cursor to join point. |
| `Delete()` | If `_cursorColumn < line.Length`: remove char at column. If at end and not last line: merge next line into current. |
| `MoveLeft()` | Decrement column; if < 0, move to previous line's end (if any). |
| `MoveRight()` | Increment column; if past end, move to next line's start (if any). |
| `MoveUp()` | Decrement line; clamp column to min of current and new line length. |
| `MoveDown()` | Increment line; clamp column to min of current and new line length. |
| `MoveHome()` | `_cursorColumn = 0` |
| `MoveEnd()` | `_cursorColumn = _lines[_cursorLine].Length` |
| `Submit()` | Join `_lines` with `\n`, emit `OnSubmit`, clear `_lines` to single empty line, reset cursor. |

### 5.3 Submit key discipline

| Key | Action |
|-----|--------|
| `Enter` (no modifier) | Insert newline (`\n`) — does **not** submit |
| `Shift+Enter` | Insert newline |
| `Ctrl+Enter` | **Submit** the message |
| `Ctrl+D` | Submit (existing exit key, keep for compatibility) |

Update `InputEditorWidget.HandleKey`:
- `Key.Enter` without `Control` modifier → insert newline
- `Key.Enter` with `Control` modifier → `Submit()`

Update `AppLayout.OnTerminalEvent` to not intercept plain `Enter` — let `InputEditorWidget` handle it.

### 5.4 History integration

History stores raw strings (can contain `\n`). When loading a history entry:
- Split on `\n` into `_lines`
- Set `_cursorLine = _lines.Count - 1`, `_cursorColumn = lastLine.Length`

### 5.5 Measure / Arrange / Render

**`Measure`:**
```csharp
public Size Measure(Size available)
{
    int lineCount = _lines.Count;
    int popupHeight = /* existing completion logic */;
    return new Size(available.Width, lineCount + popupHeight);
}
```

**`Render`:**
- For each line `i` in `_lines`:
  - Line 0: draw `"> "` prefix in warm gold + line content
  - Lines 1+: draw `"  "` continuation indent + line content
- Cursor: compute `(screenRow, screenCol)` from `(_cursorLine, _cursorColumn)`
  - Account for prompt prefix width on line 0
  - Use `CellWidthCalculator` for correct CJK/emoji positioning
- Draw cursor as inverted block (existing logic, extended to multi-line)

**Completion popup:**
- Draw above the input area, anchored to the top of the widget's bounds
- Input area grows downward from the popup

### 5.6 AppLayout layout impact

The `VStack` in `AppLayout` already uses:
```csharp
_root.Add(_input); // dynamic height
```

No changes needed to `AppLayout` layout logic — `InputEditorWidget.Measure` now returns the correct height, and `VStack` will allocate space accordingly. The transcript viewport (`FlexSizeWidget`) will shrink automatically.

**Optional:** Add a `MaxInputLines` property (default 10) to prevent the input from consuming the entire terminal.

---

## 6. Phase C: Testing

### 6.1 Parser tests

| Test | Description |
|------|-------------|
| `BracketedPaste_SingleLine` | `ESC[200~helloESC[201~` → single `PasteEvent("hello")` |
| `BracketedPaste_MultiLine` | `ESC[200~a\nbESC[201~` → `PasteEvent("a\nb")` |
| `BracketedPaste_NoLeak` | Content inside paste envelope does NOT emit `KeyEvent`s |
| `BracketedPaste_SplitAcrossReads` | `ESC[200~` in read 1, `helloESC[201~` in read 2 → single event |
| `BracketedPaste_Empty` | `ESC[200~ESC[201~` → `PasteEvent("")` |

### 6.2 Input editor tests

| Test | Description |
|------|-------------|
| `MultiLine_InsertText` | `Insert("abc\ndef")` → 2 lines, cursor on line 1 col 3 |
| `MultiLine_BackspaceAtStart` | Backspace at col 0 of line 1 → merges with line 0 |
| `MultiLine_DeleteAtEnd` | Delete at end of line 0 → merges with line 1 |
| `MultiLine_CursorUp` | Up from line 1 → line 0, clamped column |
| `MultiLine_CursorDown` | Down from line 0 → line 1, clamped column |
| `MultiLine_Enter_InsertsNewline` | Plain Enter → new line, no submit |
| `MultiLine_CtrlEnter_Submits` | Ctrl+Enter → `OnSubmit` fires with joined text |
| `MultiLine_Submit_JoinsWithNewline` | Submit joins lines with `\n` |
| `MultiLine_History_RoundTrip` | Submit, navigate history back, verify lines restored |
| `MultiLine_PasteEvent` | Simulate `PasteEvent("line1\nline2")` → 2 lines in editor |

---

## 7. Phase D: Rollout Order

| Step | File(s) | Risk | Est. time |
|------|---------|------|-----------|
| 1. Add `PasteEvent` | `TerminalEvent.cs` | None | 5 min |
| 2. Parse bracketed paste | `SystemTerminalBackend.cs` | Low | 45 min |
| 3. Wire `PasteEvent` to input | `AppLayout.cs`, `InputEditorWidget.cs` | Low | 15 min |
| 4. Multi-line data model | `InputEditorWidget.cs` | Medium | 1 hr |
| 5. Multi-line render | `InputEditorWidget.cs` | Medium | 1 hr |
| 6. Ctrl+Enter submit | `InputEditorWidget.cs`, `AppLayout.cs` | Low | 15 min |
| 7. History with newlines | `InputEditorWidget.cs` | Low | 30 min |
| 8. Tests | New test file | Low | 1 hr |

**Total: ~5 hours**

---

## 8. Open Questions

1. **Soft-wrap vs. horizontal scroll:** Should long lines soft-wrap visually or scroll horizontally? Soft-wrap is more user-friendly but adds cursor-mapping complexity. **Recommendation:** horizontal scroll for MVP, soft-wrap as follow-up.
2. **Auto-indent:** When pressing Enter inside a code block, should the next line inherit indentation? **Recommendation:** no auto-indent for MVP.
3. **Line numbers:** Show `1>`, `2>` prompts or just `>` on first line? **Recommendation:** `>` on line 0, spaces on continuations (minimal change).
4. **Max input height:** Cap at N lines to prevent transcript squashing? **Recommendation:** default 10 lines, configurable.
