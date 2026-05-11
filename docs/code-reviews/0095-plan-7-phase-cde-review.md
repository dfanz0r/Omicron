# Code Review 0095: Plan 7 Phases C, D, E — Terminal Backend, Frame Buffer, TUI Shell

**Status:** Review Complete — 6 Critical / High findings, 5 Medium, 4 Low  
**Date:** 2026-05-10  
**Build:** 0 errors, 0 warnings, 624 tests pass (34 new)  
**Files Reviewed:** 20 new + 1 modified

---

## Executive Summary

The implementation is structurally sound and the test coverage is good for an MVP. However, **three Critical/High bugs break core correctness**: non-ASCII glyphs are lost during frame diffing, space cells that clear old glyphs are not emitted, and Unix raw mode disables `ISIG` which prevents crash-safe `Ctrl+C` recovery. These must be fixed before the renderer can be used for real transcript output.

---

## Critical Findings

### Finding 1 — CRITICAL: `DifferentialRenderer` loses non-ASCII glyphs after first frame

**File:** `Omicron.Core/Rendering/DifferentialRenderer.cs`  
**Severity:** Critical  
**Category:** Correctness

#### Problem

`Render()` copies the source frame's `Cells` array into the swap-chain's current back buffer:

```csharp
Array.Copy(source.Cells, current.Cells, source.Cells.Length);
```

But it does **not** copy the `GlyphInternTable`. `TerminalFrame` constructs a fresh empty `GlyphInternTable`. After `Array.Copy`, the back buffer contains `GlyphRef` intern IDs that point to `source.GlyphTable`, but `current.GlyphTable` is empty. When `Diff()` or `EmitFullFrame()` calls `AnsiEncoder.WriteGlyph(..., cur.GlyphTable)`, `Resolve(id)` returns `Empty`, so the glyph is silently dropped.

#### Reproducer

```csharp
var renderer = new DifferentialRenderer(10, 5);
var frame = new TerminalFrame(10, 5);
frame.SetText(0, 0, "一"u8, TextStyle.Default);

// First render — full frame, uses frame.GlyphTable directly
renderer.Render(frame, output1);

// Second render — diff, uses swap-chain current.GlyphTable (empty)
var output2 = new ArrayBufferWriter<byte>();
renderer.Render(frame, output2);
// output2 will NOT contain the CJK glyph bytes
```

#### Fix

Add `GlyphInternTable.CopyFrom(GlyphInternTable other)` that copies `_entries` (preserving IDs) and rebuilds `_table`. In `DifferentialRenderer.Render()`:

```csharp
Array.Copy(source.Cells, current.Cells, source.Cells.Length);
current.GlyphTable.CopyFrom(source.GlyphTable);
```

Alternative (simpler but less efficient): make `TerminalFrame` accept an external `GlyphInternTable` that is shared between the source and the swap chain. But sharing requires `Swap()` not to `Clear()` the table. The `CopyFrom` approach is safest.

#### Test Gap

`DifferentialRenderer_WideCharacter_RespectsContinuationCells` only asserts that the second render emits **nothing** (because the frame is identical). It does not verify that the glyph bytes would actually be emitted if a change occurred. Add a test that changes a neighboring cell and asserts the CJK glyph is still present in the diff output.

---

### Finding 2 — CRITICAL: `SwapChain.Diff` skips space cells, leaving ghost characters on screen

**File:** `Omicron.Core/Rendering/SwapChain.cs` (lines ~130–150)  
**Severity:** Critical  
**Category:** Correctness

#### Problem

In the diff emit loop:

```csharp
if (cell.Width == 0)
    continue;
if (cell.IsEmpty)
    continue; // ← skips space cells
```

If a cell changes from `'X'` to space (e.g., backspace, truncation, or layout shift), the diff does **not** write a space to clear the old glyph. The terminal still shows `'X'`. Worse, the cursor does not advance over the skipped column, so all subsequent glyphs in the run are shifted left by one.

#### Reproducer

```csharp
// Frame 1: "hello"
frame1.SetText(0, 0, "hello"u8, style);
renderer.Render(frame1, output1);

// Frame 2: "hell " (clear last char)
frame2.SetText(0, 0, "hell "u8, style);
renderer.Render(frame2, output2);
// Terminal still shows "hello" because the 'o'→space change is skipped
```

#### Fix

Write an explicit space for empty cells (width > 0):

```csharp
if (cell.Width == 0)
    continue;

if (cell.IsEmpty)
{
    Span<byte> space = stackalloc byte[1] { (byte)' ' };
    output.Write(space);
}
else
{
    AnsiEncoder.WriteGlyph(cell.Glyph, output, cur.GlyphTable);
}
```

Also apply the same fix to `DifferentialRenderer.EmitFullFrame` if it ever runs on a non-cleared screen (e.g., after resize without clear).

#### Test Gap

Add a test: `DifferentialRenderer_CellCleared_EmitsSpace` that verifies a cell changing from `'X'` to space results in a space byte in the diff output.

---

## High Findings

### Finding 3 — HIGH: Unix raw mode disables `ISIG`, breaking `Ctrl+C` crash recovery

**File:** `Omicron.Core/Rendering/SystemTerminalBackend.cs` (line ~120)  
**Severity:** High  
**Category:** Reliability / Crash Safety

#### Problem

On Unix, the constructor disables `ISIG`:

```csharp
raw.c_lflag &= ~(ECHO | ICANON | ISIG);
```

Disabling `ISIG` prevents the TTY driver from generating `SIGINT` on `Ctrl+C`. The `TerminalLifecycle.OnCancelKeyPress` handler (which hooks `Console.CancelKeyPress`) is triggered by `SIGINT` on Unix. With `ISIG` off, `Ctrl+C` sends raw byte `0x03` (ETX) to the input stream instead of raising `SIGINT`. Therefore:

1. `TerminalLifecycle.EmergencyRestore()` is **never called** on `Ctrl+C`.
2. The terminal is left in raw mode with a hidden cursor and alternate screen active.
3. The user has no clean way to exit the TUI on Unix except closing the terminal window.

#### Fix

Remove `ISIG` from the mask:

```csharp
raw.c_lflag &= ~(ECHO | ICANON); // keep ISIG enabled
```

This allows `Ctrl+C` to generate `SIGINT`, which triggers `Console.CancelKeyPress`, which calls `EmergencyRestore()` and exits cleanly.

If you want `Ctrl+C` to be readable as a key event (useful for copy/paste), handle it in the app layer by checking `ke.Text?.Value == 3` and explicitly exiting. But **never** disable `ISIG` without an alternative crash-recovery mechanism.

---

### Finding 4 — HIGH: `SystemTerminalBackend.Dispose()` closes process stdout

**File:** `Omicron.Core/Rendering/SystemTerminalBackend.cs` (line ~345)  
**Severity:** High  
**Category:** Reliability

#### Problem

```csharp
_outputStream.Dispose();
```

`_outputStream` is `Console.OpenStandardOutput()`. Disposing the process stdout stream is undefined behavior. Subsequent writes to `Console.Out` (e.g., after TUI exits back to console mode) will throw `ObjectDisposedException`.

#### Fix

Remove `_outputStream.Dispose()` from `Dispose()`. The stream is owned by the process, not the backend.

---

### Finding 5 — HIGH: `TerminalLifecycle.EmergencyRestore` cannot enumerate tracked backends

**File:** `Omicron.Core/Rendering/TerminalLifecycle.cs` (lines ~85–110)  
**Severity:** High  
**Category:** Crash Safety

#### Problem

`EmergencyRestore()` iterates over `EnumerateActiveScopes()`, which returns an **empty list** because `ConditionalWeakTable` has no public enumeration API:

```csharp
private static IEnumerable<(...)> EnumerateActiveScopes()
{
    var result = new List<(...)>();
    // We rely on the WeakTable — we can't enumerate it directly.
    return result; // always empty
}
```

The comment admits this. So crash recovery via `AppDomain.ProcessExit` or `Console.CancelKeyPress` does **nothing**.

#### Fix

Since there is typically only one active terminal backend per process, track it explicitly:

```csharp
private static ITerminalBackend? _lastBackend;
private static readonly HashSet<TerminalScope.ScopeType> _lastTypes = new();

internal static void Track(ITerminalBackend backend, TerminalScope.ScopeType type)
{
    _lastBackend = backend;
    _lastTypes.Add(type);
}

internal static void Untrack(ITerminalBackend backend, TerminalScope.ScopeType type)
{
    _lastTypes.Remove(type);
}

public static void EmergencyRestore()
{
    if (_lastBackend is null) return;
    foreach (var type in _lastTypes) { /* emit exit sequences */ }
}
```

For MVP, a single static backend reference is sufficient. A full `List<WeakReference<ITerminalBackend>>` can be added later if multiple backends are needed.

---

### Finding 6 — HIGH: `SystemTerminalBackend.RefreshSize` uses stdin for `ioctl(TIOCGWINSZ)`

**File:** `Omicron.Core/Rendering/SystemTerminalBackend.cs` (line ~305)  
**Severity:** High  
**Category:** Portability

#### Problem

```csharp
ioctl(_stdinFd, TIOCGWINSZ, ref wsz) // _stdinFd = 0
```

`ioctl(TIOCGWINSZ)` should be called on the **controlling terminal**, which is typically stdout (fd 1) or stderr (fd 2). If stdin is redirected (e.g., `echo "foo" | omicron --tui`), fd 0 is a pipe, not a TTY, and `ioctl` fails. The fallback `Console.WindowWidth` throws on Unix when not connected to a terminal.

#### Fix

Use stdout for the ioctl:

```csharp
private const int STDOUT_FILENO = 1;
// ...
if (ioctl(STDOUT_FILENO, TIOCGWINSZ, ref wsz) == 0 && wsz.ws_col > 0)
```

Or open `/dev/tty` as a fallback.

---

## Medium Findings

### Finding 7 — MEDIUM: `SystemTerminalBackend.ReadEventsImpl` doesn't handle UTF-8 multi-byte input

**File:** `Omicron.Core/Rendering/SystemTerminalBackend.cs` (line ~170)  
**Severity:** Medium  
**Category:** Correctness

#### Problem

```csharp
byte b = buffer[offset++];
var rune = new Rune(b);
yield return new KeyEvent(Key.Character, KeyModifiers.None, rune);
```

Every byte above `0x7F` is treated as an invalid standalone `Rune`. Typing "é" (UTF-8: `0xC3 0xA9`) yields two `KeyEvent`s with invalid runes instead of one with `é`. CJK and emoji input is completely broken.

#### Fix

Accumulate bytes and use `Rune.DecodeFromUtf8`:

```csharp
// maintain a small leftover buffer between reads
if (Rune.DecodeFromUtf8(buffer.AsSpan(offset), out var rune, out int consumed) == OperationStatus.Done)
{
    offset += consumed;
    yield return new KeyEvent(Key.Character, KeyModifiers.None, rune);
}
```

Handle `NeedMoreData` by copying trailing bytes to a pending buffer for the next `ReadAsync`.

---

### Finding 8 — MEDIUM: `AnsiEncoder` allocates strings/byte arrays for every cursor move and style change

**File:** `Omicron.Core/Rendering/AnsiEncoder.cs`  
**Severity:** Medium  
**Category:** Performance

#### Problem

`SetCursorPosition` and `SetStyle` use `Encoding.UTF8.GetBytes($"...")` on every call. In a diff with 50 changed runs, this allocates ~50 strings and 50 byte arrays per frame. At 60 FPS, that's 3,000 allocations/sec just for cursor positioning.

#### Fix

Write ASCII digits directly into a `Span<byte>`:

```csharp
public static void SetCursorPosition(int row, int col, IBufferWriter<byte> output)
{
    Span<byte> buf = stackalloc byte[32];
    int pos = 0;
    buf[pos++] = 0x1B; buf[pos++] = (byte)'[';
    pos += WriteInt(row + 1, buf.Slice(pos));
    buf[pos++] = (byte)';';
    pos += WriteInt(col + 1, buf.Slice(pos));
    buf[pos++] = (byte)'H';
    output.Write(buf.Slice(0, pos));
}

private static int WriteInt(int value, Span<byte> dest)
{
    // simple integer-to-ASCII, no allocation
}
```

Same for the RGB style sequences.

---

### Finding 9 — MEDIUM: `GlyphInternTable` allocates a string on every non-ASCII lookup

**File:** `Omicron.Core/Rendering/GlyphInternTable.cs` (line ~45)  
**Severity:** Medium  
**Category:** Performance

#### Problem

```csharp
string key = Encoding.UTF8.GetString(utf8);
```

Every non-ASCII grapheme cluster allocates a string for dictionary lookup, even for cache hits. For a frame with 500 CJK characters, that's 500 string allocations per frame.

#### Fix

Use `Dictionary<ReadOnlyMemory<byte>, int>` with a custom `IEqualityComparer<ReadOnlyMemory<byte>>` that compares byte spans. Or use a trie / hash map over `ReadOnlySpan<byte>`. For MVP, note in comments as a future optimization.

---

### Finding 10 — MEDIUM: `TerminalScope.UseRawMode` is a no-op

**File:** `Omicron.Core/Rendering/TerminalScope.cs` (line ~45)  
**Severity:** Medium  
**Category:** Design Consistency

#### Problem

```csharp
public static TerminalScope UseRawMode(ITerminalBackend backend)
    => Acquire(backend, ScopeType.RawMode, null); // enterSequence is null
```

It increments a counter but never actually sets raw mode. Raw mode is instead set eagerly in `SystemTerminalBackend` constructor. This is confusing and breaks the scope pattern.

#### Fix

Either:
- Make `UseRawMode` emit the platform-specific raw-mode sequence (not applicable; raw mode is a state change, not an ANSI sequence), or
- Remove `UseRawMode` from the public API and document that raw mode is always active while the backend is alive, or
- Move raw mode setup from the constructor into `UseRawMode` by having the backend expose `EnterRawMode()` / `ExitRawMode()` methods that the scope calls.

---

### Finding 11 — MEDIUM: `ScopeCounter` is not thread-safe

**File:** `Omicron.Core/Rendering/TerminalScope.cs` (lines ~115–130)  
**Severity:** Medium  
**Category:** Thread Safety

#### Problem

```csharp
public void Increment(ScopeType type) => _counts[(int)type]++;
public int Decrement(ScopeType type)
{
    if (_counts[(int)type] > 0) _counts[(int)type]--;
    return _counts[(int)type];
}
```

Non-atomic increments/decrements on an `int[]`. Concurrent scope acquisition from multiple threads can corrupt counts.

#### Fix

Use `Interlocked`:

```csharp
public void Increment(ScopeType type) => Interlocked.Increment(ref _counts[(int)type]);
public int Decrement(ScopeType type) => Interlocked.Decrement(ref _counts[(int)type]);
```

---

## Low Findings

### Finding 12 — LOW: `SystemTerminalBackend` eagerly modifies terminal state in constructor

**File:** `Omicron.Core/Rendering/SystemTerminalBackend.cs` (constructor)  
**Severity:** Low  
**Category:** Design

#### Problem

Creating a `SystemTerminalBackend` immediately enables VT processing and raw mode. This is surprising for a type that also exposes a scope-based API (`TerminalScope`). A consumer might create the backend to query `Size` before deciding to enter TUI mode, and find their terminal already altered.

#### Fix

Move platform initialization into an explicit `Initialize()` or `EnterRawMode()` method. Call it from `TuiShell.RunAsync` before entering the alternate screen. Keep the constructor minimal (just open the output stream).

---

### Finding 13 — LOW: `GlyphRef.Replacement` points to non-existent intern ID 65533

**File:** `Omicron.Core/Rendering/GlyphRef.cs` (line ~20)  
**Severity:** Low  
**Category:** Correctness

#### Problem

```csharp
public static GlyphRef Replacement => new(0x8000FFFD); // interned, ID = 65533
```

A fresh `GlyphInternTable` has no entry at ID 65533. `AnsiEncoder.WriteGlyph` resolves it to `Empty` and writes nothing. The fallback `\uFFFD` UTF-8 bytes are never reached because `internTable` is not null.

#### Fix

Make `Replacement` an ASCII fallback:

```csharp
public static GlyphRef Replacement => Ascii((byte)'?');
```

Or pre-seed every `GlyphInternTable` with U+FFFD at ID 0xFFFD (overkill for MVP).

---

### Finding 14 — LOW: `SystemTerminalBackend` mouse and resize events don't work on Windows

**File:** `Omicron.Core/Rendering/SystemTerminalBackend.cs`  
**Severity:** Low  
**Category:** Feature Completeness

#### Problem

On Windows, `ENABLE_MOUSE_INPUT` and `ENABLE_WINDOW_INPUT` cause the console to emit `INPUT_RECORD` structures, not byte sequences. `ReadEventsImpl` reads bytes via `Console.OpenStandardInput()`, so mouse and resize events are silently lost.

#### Fix

On Windows, use `ReadConsoleInput` P/Invoke to read `INPUT_RECORD` structs and map `KEY_EVENT`, `MOUSE_EVENT`, `WINDOW_BUFFER_SIZE_EVENT` to `TerminalEvent`. This is a significant refactor; for MVP, document the limitation.

---

### Finding 15 — LOW: `SimpleStatusBar` uses string `.Length` for layout alignment

**File:** `Omicron.CLI/Tui/SimpleStatusBar.cs` (lines ~40–55)  
**Severity:** Low  
**Category:** Correctness (i18n)

#### Problem

```csharp
int centerCol = (frame.Width - provider.Length) / 2;
int rightCol = frame.Width - rightText.Length - 1;
```

String `.Length` is Unicode scalar count, not terminal cell width. A provider name like "OpenAI" is fine, but "日本語" has `Length == 3` and cell width 6, so it will be misaligned.

#### Fix

Use `CellWidthCalculator.GetWidth` on each rune to compute display width. Or document the limitation for MVP.

---

## Observations (Non-blocking)

### Observation A — Missing test: cell-clearing diff

No test verifies that a cell changing from a glyph to space emits a space in the diff output. Add `DifferentialRenderer_CellCleared_EmitsSpace`.

### Observation B — Missing test: non-ASCII glyph survives across swap-chain frames

No test renders non-ASCII text, swaps buffers, diffs, and asserts the glyph bytes are present in the second frame's output. Add `DifferentialRenderer_CjkGlyph_SurvivesSwap`.

### Observation C — `TuiShell` frame pacing blocks event reading

`Task.Delay(frameMs)` after rendering means keyboard input is not polled during the delay. At 60 FPS this is ~16 ms — acceptable for MVP but should be replaced with a `PeriodicTimer` or concurrent read loop later.

### Observation D — `SystemTerminalBackend` escape parser allocates per sequence

`Encoding.ASCII.GetString` + `string.Split(';')` on every CSI sequence creates garbage. This is acceptable for MVP but should be replaced with span-based parsing in a future hardening pass.

### Observation E — `TerminalFrame.SetText` drops combining marks

Width-0 graphemes (combining marks) are skipped entirely, so "e + combining acute" renders as just "e". Full combining-mark support requires overlaying the mark onto the previous cell's glyph or using a precomposed form. Documented limitation for MVP.

---

## Fix Priority Table

| Priority | Finding | Files | Effort |
|----------|---------|-------|--------|
| P0 | 1 — GlyphInternTable not copied | `DifferentialRenderer.cs`, `GlyphInternTable.cs` | Small |
| P0 | 2 — Space cells skipped in diff | `SwapChain.cs`, `DifferentialRenderer.cs` | Small |
| P1 | 3 — Unix ISIG disabled | `SystemTerminalBackend.cs` | Tiny |
| P1 | 4 — Dispose closes stdout | `SystemTerminalBackend.cs` | Tiny |
| P1 | 5 — EmergencyRestore enumerates empty list | `TerminalLifecycle.cs` | Small |
| P1 | 6 — ioctl on stdin | `SystemTerminalBackend.cs` | Tiny |
| P2 | 7 — UTF-8 multi-byte input | `SystemTerminalBackend.cs` | Medium |
| P2 | 8 — AnsiEncoder allocations | `AnsiEncoder.cs` | Medium |
| P2 | 9 — GlyphInternTable string allocations | `GlyphInternTable.cs` | Medium |
| P2 | 10 — UseRawMode no-op | `TerminalScope.cs`, `SystemTerminalBackend.cs` | Small |
| P2 | 11 — ScopeCounter thread safety | `TerminalScope.cs` | Tiny |
| P3 | 12 — Eager constructor side effects | `SystemTerminalBackend.cs` | Small |
| P3 | 13 — Replacement glyph invalid | `GlyphRef.cs` | Tiny |
| P3 | 14 — Windows mouse/resize | `SystemTerminalBackend.cs` | Large |
| P3 | 15 — StatusBar string.Length | `SimpleStatusBar.cs` | Tiny |

---

## Regression Checklist

After applying fixes, verify:

- [ ] `dotnet test` still passes 624 tests (0 failures, 0 warnings)
- [ ] New test: `DifferentialRenderer_CjkGlyph_SurvivesSwap` — renders CJK, swaps, diffs, asserts glyph bytes in output
- [ ] New test: `DifferentialRenderer_CellCleared_EmitsSpace` — asserts space byte emitted when cell changes to empty
- [ ] `SystemTerminalBackend.Dispose()` does not throw when called twice
- [ ] `TuiMode.RunAsync` exits cleanly on Unix with `Ctrl+C` (cursor visible, alternate screen exited)
- [ ] `SystemTerminalBackend` reports correct size when stdin is redirected but stdout is a TTY

---

## Verdict

**Do not proceed to Plan 7.1 (Transcript Viewport) until P0 and P1 findings are resolved.**

The frame buffer and diff renderer are the foundation for all downstream TUI work. If non-ASCII glyphs are lost and cells cannot be cleared, the transcript viewport will be unusable for any real content. The three P0/P1 correctness fixes are small (each < 20 lines) but essential.
