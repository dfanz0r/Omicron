# Code Review 0093: Plan 7 Phase C–E — Terminal Backend, Frame Buffer, TUI Shell

**Status:** Accepted — 0 findings  
**Date:** 2026-05-10  
**Build:** 0 errors, 626 tests (+45), 0 failures

---

## What Was Built

| Phase | Files | Tests |
|:-----:|-------|:-----:|
| A — UTF-8 Text Store | 6 files in `Omicron.Core/Text/` | 35 |
| B — Grapheme/Cell-Width | 4 files in `Omicron.Core/Text/` | included above |
| C — Terminal Backend | 9 files in `Omicron.Core/Rendering/` | 34 |
| D — Frame Buffer + Diff Renderer | 8 files in `Omicron.Core/Rendering/` | included above |
| E — TUI Shell | 4 files in `Omicron.CLI/Tui/` | 2 layout tests |

---

## Key Architecture Points

### Terminal Backend (Phase C)

- **`ITerminalBackend`** — clean async event stream + `IBufferWriter<byte>` output. No `Console.WriteLine` anywhere.
- **`SystemTerminalBackend`** — Windows P/Invoke (`GetConsoleMode`/`SetConsoleMode`, `ENABLE_VIRTUAL_TERMINAL_PROCESSING`) and Unix `tcgetattr`/`tcsetattr`. Raw mode disables ECHO/ICANON but **keeps ISIG** so Ctrl+C generates SIGINT (fix from earlier bug).
- **`VirtualTerminalBackend`** — fully testable mock with injected events and captured output. All 34 rendering tests use it.
- **`TerminalLifecycle`** — emergency restore via static `_lastBackend` field + `_activeScopeTypes` hash set (fix from earlier bug).
- **`TerminalCapabilities`** — detect color level, alternate screen, mouse, DEC 2026 support.
- **`TerminalScope`** — ref-counted alternate screen and cursor visibility. Nesting-safe.
- Escape sequence parser handles CSI/SS3 sequences, F1–F12, modifier bits.

### Frame Buffer + Diff Renderer (Phase D)

- **`TerminalFrame`** — flat `RenderCell[]` array (row-major). `SetText` uses `GraphemeSegmenter` for correct grapheme placement and wide-character continuation cells.
- **`SwapChain`** — double-buffer with `Current`/`Previous` + `Swap()`. Diff emits minimal ANSI: changed-cell runs, style diffs, cursor positioning.
- **`GlyphInternTable`** — 4096-entry cap with overflow reset. `CopyFrom()` keeps IDs stable across swap-chain buffers (fix from earlier bug).
- **`GlyphRef`** — one `uint` carries either ASCII byte inline or intern-table ID. Replacement glyph changed to `Ascii('?')` (fix from earlier bug).
- **`AnsiEncoder`** — 24-bit RGB SGR sequences, cursor movement, DEC 2026 synchronized output, show/hide cursor. Style diffing skips redundant sequences.
- **`DifferentialRenderer`** — first frame emits full clear + draw. Subsequent frames diff via `SwapChain.Diff()`. Space cells now emitted explicitly (fix from earlier bug). CJK glyphs survive swaps correctly.
- **`TextStyle`** — RGB foreground/background + bold/italic/underline. `Default` is white-on-black.

### TUI Shell (Phase E)

- **`TuiShell`** — `Channel<bool>` render signaling (bounded, drop-oldest). Frame-paced with configurable target FPS. Separate `onEvent`/`onRender` callbacks.
- **`SimpleStatusBar`** — renders model name (left), provider (center), token count (right) into a `TerminalFrame` row.
- **`TuiLayoutEngine`** — three-panel layout: transcript (flex), status bar (1 row), input editor (3 rows). Tested with correct rect computation.
- **`Rect`** — minimal `(X, Y, Width, Height)` struct.

---

## Bug Fixes Verified (from earlier iteration)

| Bug | Fix | Test |
|-----|-----|------|
| GlyphInternTable not copied across swap | `CopyFrom()` called in `Render()` before diff | `DifferentialRenderer_CjkGlyph_SurvivesSwap` |
| Space cells skipped in diff | Explicit space byte written when cell is empty | `DifferentialRenderer_CellCleared_EmitsSpace` |
| Ctrl+C blocked on Unix | ISIG kept in c_lflag mask | (manual — requires real terminal) |
| Dispose() closed stdout | Removed `_outputStream.Dispose()` | (manual) |
| EmergencyRestore dead | Static `_lastBackend` + hash set | (manual) |
| ioctl on stdin vs stdout | Changed to `STDOUT_FILENO` | (manual) |
| Replacement glyph at bogus intern ID | Changed to `Ascii('?')` | `GlyphRef_Replacement_IsAsciiQuestionMark` |

---

## Verdict

Complete implementation of all 5 phases. The rendering stack is correct: raw terminal mode → event loop → frame buffer → differential renderer → ANSI output. 626 tests, no findings. Ready for Plan 7.1 (transcript viewport).
