# Implementation Plan 0009: Kitty Keyboard Protocol Support

**Status:** Draft
**Date:** 2026-05-12
**Area:** `Omicron.Core/Rendering/`, `Omicron.CLI/Tui/`

---

## 1. Problem Statement

The current input system is designed around legacy terminal conventions where:

- Printable characters arrive as raw UTF-8 bytes
- Special keys (arrows, function keys) arrive as escape sequences
- Control characters (Ctrl+C, Ctrl+D) arrive as C0 codes (`0x01`–`0x1F`)
- Enter sends `\r`, Backspace sends `0x7F`, Tab sends `0x09`

The kitty keyboard protocol changes this fundamentally. When `report_all_keys` is enabled:

- **Every** key event becomes a `CSI` sequence: `CSI key-code ; modifiers [:event-type] u`
- The `key-code` is the **un-shifted** Unicode codepoint (e.g. `49` for `1`, even when Shift is held)
- Shifted text (`!`) is either derived from the key-code + Shift modifier, or sent as a separate sub-parameter
- Control characters (Ctrl+D = codepoint `4`) arrive as `CSI 4;5 u`, not as raw `0x04`
- Release and repeat events are explicit (via `:3` / `:2` sub-parameters)
- Plain Enter arrives as `CSI 13 u`, not as `\r`

Our current code tries to bolt kitty support onto a legacy model, which causes:

1. **Shift+Enter** → `CSI 13;2 u` is parsed, but bare `\r` fallback conflicts
2. **Shift+1** → `CSI 49;2 u`; parser produces `KeyEvent(Key.Character, Shift, '1')` — the widget inserts `1` instead of `!`
3. **Ctrl+D** → `CSI 4;5 u`; parser produces `KeyEvent(Key.None, Control, null)` — app-level `ke.Text?.Value == 4` check fails
4. **Modifier-only keys** → PUA codepoints (57344+) leak as garbage characters in VS Code terminal
5. **Windows Terminal < v1.22** → ignores kitty protocol entirely; requires fallback

## 2. Goals

1. **Unified input model** — widgets consume `KeyEvent` without knowing which protocol is active
2. **Correct text production** — Shift+1 produces `!`, Ctrl+Shift+A produces `\x01`, etc.
3. **Clean fallback** — legacy terminals work; kitty-capable terminals get enhanced input
4. **No Windows-specific polling** in the happy path; reserve `GetAsyncKeyState` for detected dumb terminals only
5. **Future-proof** — support event types (press/repeat/release) and text-as-codepoints for IME/compose

## 3. Non-Goals

- Full kitty protocol compliance (event types, alternate keys, base layout keys) for MVP
- IME composition support
- Numpad / function key exhaustive mapping
- Removing legacy support entirely

## 4. Design

### 4.1 Redesigned `KeyEvent` record

```csharp
public sealed record KeyEvent(Key Key, KeyModifiers Modifiers, Rune? Text) : TerminalEvent
{
    /// <summary>
    /// The raw key code from the kitty protocol (Unicode codepoint of the
    /// un-shifted key).  Null for legacy events that didn't come through
    /// the kitty protocol.
    /// </summary>
    public int? KeyCode { get; init; }

    /// <summary>
    /// The text that would be produced by this key combination in the
    /// current keyboard layout.  Computed from KeyCode + Modifiers when
    /// the kitty protocol is active.  Null when unknown.
    /// </summary>
    public string? ResolvedText { get; init; }

    /// <summary>
    /// Event type for kitty protocol (press/repeat/release).  Always
    /// <see cref="KeyEventType.Press"/> for legacy terminals.
    /// </summary>
    public KeyEventType EventType { get; init; } = KeyEventType.Press;
}
```

Add:

```csharp
public enum KeyEventType { Press, Repeat, Release }
```

### 4.2 Kitty parser (`TryParseKittyKeySequence`)

Extracted as a dedicated method that runs **before** the legacy escape parser:

```csharp
private bool TryParseKittyKeySequence(
    byte[] buffer, ref int offset, int length,
    out TerminalEvent? terminalEvent)
```

Handles the format:

```
CSI unicode-key-code [:shifted-key][:base-layout-key] ; modifiers [:event-type] [; text-as-codepoints] u
```

Parsing rules:

1. Split parameters by `;`
2. First param: split by `:` → `keyCode`, `shiftedKey`, `baseLayoutKey`
3. Second param: split by `:` → `modifierValue`, `eventType`
4. Third param (optional): `textAsCodepoints` (colon-separated Unicode codepoints)

Modifier decoding (xterm encoding: `value = 1 + actual_modifiers`):

```
1  = none
2  = shift
3  = alt
4  = shift+alt
5  = ctrl
6  = shift+ctrl
7  = alt+ctrl
8  = shift+alt+ctrl
```

Key code to `Key` mapping:

| Key Code | Key value |
|----------|-----------|
| 1–26 | `Key.Character` (control char) |
| 9 | `Key.Tab` |
| 13 | `Key.Enter` |
| 27 | `Key.Escape` |
| 32–126 | `Key.Character` |
| 127 | `Key.Backspace` |
| 57344–63743 | `Key` from functional key table |
| other | `Key.None` |

**Text resolution** (critical for widget compatibility):

```csharp
string? ResolveText(int keyCode, KeyModifiers modifiers, int? shiftedKey, string? textAsCodepoints)
{
    // If the terminal sent explicit text (report_text flag), use it
    if (textAsCodepoints is not null)
        return DecodeCodepoints(textAsCodepoints);

    // If shifted key is available and shift is active, use it
    if (shiftedKey.HasValue && modifiers.HasFlag(KeyModifiers.Shift))
        return char.ConvertFromUtf32(shiftedKey.Value);

    // Otherwise derive from key code + modifiers
    if (keyCode is >= 1 and <= 26 && modifiers.HasFlag(KeyModifiers.Control))
        return ((char)(keyCode)).ToString(); // Ctrl+A → "\x01"

    if (keyCode >= 32 && keyCode < 0x110000)
        return char.ConvertFromUtf32(keyCode);

    return null;
}
```

### 4.3 Legacy parser (`TryParseEscapeSequence`)

Unchanged for backward compatibility. Continues to handle:
- `CSI 1;2A` (Shift+Up)
- `CSI 27;2;13~` (xterm modifyOtherKeys)
- `SS3` sequences
- Plain `ESC` → Escape key

### 4.4 Event loop priority

```csharp
// 1. Kitty protocol sequences (CSI ... u with proper format)
if (TryParseKittyKeySequence(buffer, ref offset, length, out var kittyEvent))
{
    yield return kittyEvent!;
    continue;
}

// 2. Legacy escape sequences (CSI without kitty format, SS3, etc.)
if (TryParseEscapeSequence(buffer, ref offset, length, out var legacyEvent))
{
    yield return legacyEvent!;
    continue;
}

// 3. Raw UTF-8 / control characters (legacy terminals only)
var status = Rune.DecodeFromUtf8(...);
```

### 4.5 `InputEditorWidget` updates

The widget currently has this structure:

```csharp
if (ke.Key == Key.Character && ke.Text.HasValue)
{
    char c = (char)ke.Text.Value.Value;
    // ...
}
```

Updated to use `ResolvedText` when available:

```csharp
if (ke.Key == Key.Character && (ke.ResolvedText ?? ke.Text?.ToString()) is string text)
{
    Insert(text);
    return true;
}
```

Control character handling:

```csharp
// Ctrl+D exit
if (ke.Key == Key.Character && ke.Text?.Value == 4)
// → becomes:
if (ke.ResolvedText == "\x04" || ke.Text?.Value == 4)
```

Enter handling (unified):

```csharp
switch (ke.Key)
{
    case Key.Enter when ke.Modifiers.HasFlag(KeyModifiers.Shift):
    case Key.Enter when ke.Modifiers.HasFlag(KeyModifiers.Control):
        Insert("\n");
        return true;

    case Key.Enter:
        Submit();
        return true;
}
```

This already works for kitty `CSI 13;2 u` → `Key.Enter + Shift`.

### 4.6 Protocol negotiation strategy

**Current behavior (problematic):**
- Send `ESC[=9u` unconditionally
- Hope the terminal supports it

**New behavior (robust):**

Phase A — **Detection** ( startup, after alternate screen):

```
1. Send: ESC[?u        (query current kitty flags)
2. Send: ESC[c          (primary device attributes — standard VT query)
3. Start 100ms timer
4. If response received:
   - ESC[?flags u       → terminal supports kitty protocol, flags = current state
   - No response        → terminal does not support kitty protocol
5. If supported:
   - Send: ESC[=9u      (enable disambiguate + report_all_keys)
   - Set _kittyProtocolActive = true
6. If not supported:
   - Leave in legacy mode
   - Set _kittyProtocolActive = false
```

Phase B — **Runtime fallback**:

If kitty protocol is active but we receive bare `\r` (legacy Enter):
- Terminal ignored or partially ignored our request
- Use `GetAsyncKeyState` only in this case (detected legacy behavior)

This replaces the unconditional `#if OMIT_WINDOWS_KEY_FALLBACK` with runtime detection.

### 4.7 Scope changes

Remove `TerminalScope.UseKittyKeyboard()`. The protocol negotiation is **not** a scope — it's a state machine that must:
1. Query on startup
2. Enable if supported
3. Disable on exit

The reset on exit should send:
- `ESC[=0u` if we enabled it (clear all flags)
- Nothing if we detected no support

## 5. Implementation Phases

### Phase 1: Parser rework (1–2 days)

- [ ] Create `TryParseKittyKeySequence` with full sub-parameter support
- [ ] Add `KeyEvent.KeyCode`, `KeyEvent.ResolvedText`, `KeyEvent.EventType`
- [ ] Update `MapCsiSequence` to not conflict with kitty codes
- [ ] Add functional key table (PUA 57344+) → `Key` enum values
- [ ] Unit tests for kitty sequence parsing

### Phase 2: Text resolution (1 day)

- [ ] Implement `ResolveText` helper
- [ ] Handle shifted keys (Shift+1 → `!`)
- [ ] Handle control characters (Ctrl+D → `\x04`)
- [ ] Handle `report_text` embedded text
- [ ] Unit tests for text resolution

### Phase 3: Widget updates (1 day)

- [ ] Update `InputEditorWidget` to use `ResolvedText`
- [ ] Update `AppLayout` Ctrl+D / Ctrl+F checks
- [ ] Verify all key handlers work with both legacy and kitty events
- [ ] Manual test matrix (see §6)

### Phase 4: Protocol negotiation (1–2 days)

- [ ] Implement detection handshake (`ESC[?u` + timer)
- [ ] Remove `TerminalScope.UseKittyKeyboard()`
- [ ] Add `_kittyProtocolActive` flag to backend
- [ ] Send `ESC[=0u` on exit only if we enabled it
- [ ] Make Windows fallback conditional on `_kittyProtocolActive == false`
- [ ] Remove `OMIT_WINDOWS_KEY_FALLBACK` compiler define

### Phase 5: Cleanup (1 day)

- [ ] Delete `docs/kitty-keyboard-protocol.rst` (or move to `docs/references/`)
- [ ] Update `0003-opentui-windows-modifier-handling-analysis.md` with final approach
- [ ] Remove `run-kitty-test.bat` / `.ps1` / `.sh` scripts
- [ ] Final test pass on Windows Terminal, VS Code terminal, bare cmd

## 6. Test Matrix

| Terminal | Kitty Support | Enter | Shift+Enter | Ctrl+D | Shift+1 | Backspace |
|----------|--------------|-------|-------------|--------|---------|-----------|
| Windows Terminal ≥1.22 | Full | `CSI 13 u` | `CSI 13;2 u` | `CSI 4;5 u` | `CSI 49;2 u` | `CSI 127 u` |
| Windows Terminal <1.22 | None | `\r` | `\r` | `0x04` | `!` (raw) | `0x7F` |
| VS Code terminal | Partial | `\r` or `CSI 13 u` | `\r` or `CSI 13;2 u` | `0x04` or `CSI 4;5 u` | `!` (raw) or `CSI 49;2 u` | `0x7F` |
| kitty | Full | `CSI 13 u` | `CSI 13;2 u` | `CSI 4;5 u` | `CSI 49;2 u` | `CSI 127 u` |
| iTerm2 | Full | `CSI 13 u` | `CSI 13;2 u` | `CSI 4;5 u` | `CSI 49;2 u` | `CSI 127 u` |

Expected behavior in ALL cases:
- Enter → submit chat
- Shift+Enter → insert newline
- Ctrl+D → exit TUI
- Shift+1 → insert `!`
- Backspace → delete before cursor

## 7. Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| Detection timeout adds startup latency | 100ms is imperceptible; can reduce to 50ms |
| Terminal sends partial kitty response | Parser handles incomplete sequences (already does) |
| Terminal claims support but is buggy | Fallback to legacy on bare `\r` / control chars |
| PUA codepoints still leak | Functional key table maps all PUA keys to `Key` enum; never `Key.Character` |
| Release events flood the queue | Default to press-only; gate repeat/release behind future flag |

## 8. Files to Modify

- `Omicron.Core/Rendering/TerminalEvent.cs` — add `KeyEventType`, extend `KeyEvent`
- `Omicron.Core/Rendering/SystemTerminalBackend.cs` — add kitty parser, detection handshake
- `Omicron.Core/Rendering/TerminalScope.cs` — remove `UseKittyKeyboard`, `KittyKeyboard` scope type
- `Omicron.CLI/Tui/TuiShell.cs` — remove `UseKittyKeyboard` call
- `Omicron.CLI/Tui/InputEditorWidget.cs` — use `ResolvedText`
- `Omicron.CLI/Tui/AppLayout.cs` — update Ctrl+D / Ctrl+F checks
- `Omicron.Core.Tests/` — add kitty parser tests
