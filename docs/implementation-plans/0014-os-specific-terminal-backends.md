# Implementation Plan 0014: OS-Specific Low-Latency Terminal Backends

**Date:** 2026-05-15
**Status:** Planned
**Scope:** Replace the current monolithic `SystemTerminalBackend` with explicit per-OS backends and a small factory layer.

## 1. Motivation

Omicron's TUI currently depends on one cross-platform `SystemTerminalBackend` that mixes:

- Windows Console / VT setup;
- Linux `termios`, `fcntl`, `read`, `ioctl`;
- partial macOS assumptions;
- shared ANSI rendering output;
- input parsing and keyboard-protocol handling.

This has proven fragile on Linux terminals such as Kitty and Ghostty. The .NET `Console` APIs are also not appropriate for Omicron's TUI requirements because they can introduce buffering, terminal-mode side effects, poor latency, and platform-specific behavior that is hard to reason about.

The preferred direction is to use OS-native terminal APIs directly and keep each backend optimized for its OS.

## 2. Goals

1. Split terminal I/O into explicit OS-specific backends.
2. Avoid `.NET Console` input APIs in TUI mode.
3. Use low-latency native input primitives:
   - Linux: `termios` + `poll()` + `read()` + `ioctl()`.
   - macOS: separate Darwin `termios` layout + `poll()`/`read()` + Darwin `ioctl()` constants.
   - Windows: Console handles / VT mode / console input or raw stream strategy as appropriate.
4. Keep rendering backend-independent through `ITerminalBackend.Output` and ANSI encoders.
5. Keep input parsing reusable and testable.
6. Make crash cleanup safer and easier to audit.

## 3. Non-Goals

- Do not rewrite the TUI layout/rendering system.
- Do not change `TerminalFrame`, `DifferentialRenderer`, or widgets except where needed for backend lifecycle.
- Do not add GUI or PTY embedding in this plan.
- Do not perfect every terminal keyboard protocol variant; preserve current behavior and make future additions easier.

## 4. Proposed Architecture

### 4.1 Backend Factory

Add a factory:

```csharp
public static class TerminalBackendFactory
{
    public static ITerminalBackend CreateSystemBackend();
}
```

Selection:

```csharp
if (OperatingSystem.IsWindows()) return new WindowsTerminalBackend();
if (OperatingSystem.IsMacOS()) return new MacOsTerminalBackend();
if (OperatingSystem.IsLinux()) return new LinuxTerminalBackend();
return new UnsupportedTerminalBackend(...);
```

`Program.cs` should stop constructing `SystemTerminalBackend` directly.

### 4.2 File Layout

Create:

```text
Omicron.Core/Rendering/Terminal/
  ITerminalBackend.cs                  // move or keep existing interface
  TerminalBackendFactory.cs
  TerminalInputParser.cs               // shared byte parser
  TerminalModeScope.cs                 // optional helper for restore state
  WindowsTerminalBackend.cs
  Unix/
    UnixTerminalBackendBase.cs         // optional common base
    LinuxTerminalBackend.cs
    MacOsTerminalBackend.cs
    LinuxNative.cs
    MacOsNative.cs
    PollFd.cs
```

The existing `SystemTerminalBackend.cs` should either be deleted or reduced to a compatibility wrapper around the factory during transition.

## 5. Shared Parser Extraction

Move input parsing out of `SystemTerminalBackend` into a reusable parser class:

```csharp
public sealed class TerminalInputParser
{
    public IEnumerable<TerminalEvent> Parse(ReadOnlySpan<byte> bytes);
    public TerminalEvent? FlushPendingEscapeIfTimedOut(TimeSpan timeout);
}
```

Parser responsibilities:

- UTF-8 decoding;
- partial sequence buffering;
- ESC timeout handling;
- legacy CSI sequences;
- SS3 application cursor sequences;
- SGR mouse;
- bracketed paste;
- kitty keyboard protocol;
- xterm modifyOtherKeys fallback.

Backend responsibilities:

- configure terminal mode;
- read bytes with native APIs;
- feed bytes into parser;
- emit resize events;
- restore terminal mode.

This split prevents native backend work from breaking keyboard parsing.

## 6. Linux Backend

### 6.1 Native API

Use Linux-specific P/Invokes:

```csharp
tcgetattr(int fd, out LinuxTermios termios);
tcsetattr(int fd, int optionalActions, in LinuxTermios termios);
poll(PollFd[] fds, ...);
read(int fd, byte[] buffer, UIntPtr count);
ioctl(TIOCGWINSZ);
```

Linux `termios` layout:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LinuxTermios
{
    public uint c_iflag;
    public uint c_oflag;
    public uint c_cflag;
    public uint c_lflag;
    public byte c_line;
    public fixed byte c_cc[32];
    public uint c_ispeed;
    public uint c_ospeed;
}
```

### 6.2 Terminal Mode

Prefer cbreak-style raw mode:

- disable `ICANON`;
- disable `ECHO`;
- disable `IEXTEN`;
- usually keep `ISIG` enabled so Ctrl+C works;
- disable `ICRNL`, `INLCR`, and `IXON`;
- consider disabling `OPOST` only if output is fully ANSI-managed;
- set `VMIN=1`, `VTIME=0`.

Do **not** use `fcntl(O_NONBLOCK)` by default. Use `poll()` for readiness and cancellation-friendly timeouts, then `read()` only when ready.

### 6.3 Event Loop

Pseudo-code:

```csharp
while (!ct.IsCancellationRequested)
{
    if (PollStdin(timeoutMs: 10))
    {
        int n = read(stdin, buffer, buffer.Length);
        foreach (var evt in parser.Parse(buffer.AsSpan(0, n)))
            yield return evt;
    }

    if (ResizeDue())
        yield return ResizeEventIfChanged();

    if (parser.TryFlushEscapeTimeout(out var esc))
        yield return esc;
}
```

## 7. macOS Backend

Do not reuse Linux structs or constants.

macOS `termios` differs:

- flags are `nuint` / unsigned long-sized;
- no `c_line` field;
- `c_cc[20]`;
- `VMIN=16`, `VTIME=17`;
- different `TIOCGWINSZ` constant.

Create separate `MacOsNative.cs` and `MacOsTerminalBackend.cs`.

Initial macOS support can be compile-safe but runtime-minimal if not immediately tested. It should never use the Linux termios layout.

## 8. Windows Backend

Move current Windows logic into `WindowsTerminalBackend`:

- `GetStdHandle`;
- `GetConsoleMode` / `SetConsoleMode`;
- enable VT output;
- enable VT input if needed;
- restore original input/output modes;
- existing SPSC input queue / producer thread can remain initially.

Follow-up options:

1. Keep current stream-read producer thread if stable.
2. Investigate `ReadConsoleInputW` for lower-level keyboard/mouse/resize events.
3. Keep VT sequence parsing for terminals that provide VT input.

Do not block Linux/macOS refactor on Windows input redesign.

## 9. Terminal Lifecycle and Cleanup

Each backend owns its original state:

- original termios or console modes;
- original fd flags if any are changed;
- active terminal scopes;
- kitty protocol state;
- modifyOtherKeys state;
- bracketed paste / mouse state.

Cleanup requirements:

1. Restore in `Dispose()`.
2. Best-effort restore in `TerminalLifecycle.EmergencyRestore()`.
3. Restore in reverse order:
   - protocol/input extensions off;
   - mouse off;
   - bracketed paste off;
   - cursor visible;
   - alternate screen off;
   - native terminal mode restored.
4. Swallow cleanup exceptions only during emergency cleanup, not during normal initialization.

## 10. Implementation Phases

### Phase A — Prepare Interfaces

- Add `TerminalBackendFactory`.
- Replace direct `new SystemTerminalBackend()` call sites.
- Add tests that factory returns correct backend type under injectable platform abstraction, or keep minimal if platform abstraction is too much.

### Phase B — Extract Parser

- Create `TerminalInputParser`.
- Move current parsing logic from `SystemTerminalBackend`.
- Preserve current parser tests:
  - kitty sequences;
  - legacy CSI;
  - SS3 arrows;
  - bracketed paste;
  - UTF-8 partial input;
  - ESC timeout.

### Phase C — Linux Backend

- Implement Linux native API definitions.
- Use Linux cbreak mode with `poll()` + `read()`.
- Remove Linux dependence on `.NET Console.OpenStandardInput().ReadAsync`.
- Add PTY smoke tests where feasible.

### Phase D — Windows Backend

- Move existing Windows code from `SystemTerminalBackend` into `WindowsTerminalBackend`.
- Keep behavior equivalent.
- Ensure all P/Invokes are isolated to the Windows file/class.

### Phase E — macOS Backend

- Add separate macOS termios/ioctl definitions.
- Implement equivalent cbreak + poll/read loop.
- Mark runtime testing as required on macOS hardware or CI.

### Phase F — Delete or Retire `SystemTerminalBackend`

- Remove the old mixed backend once all call sites use the factory.
- Keep a small obsolete wrapper only if needed for compatibility:

```csharp
[Obsolete("Use TerminalBackendFactory.CreateSystemBackend().")]
public sealed class SystemTerminalBackend : ITerminalBackend { ... }
```

## 11. Testing Plan

### Unit Tests

- Parser tests for:
  - printable UTF-8;
  - Enter/Backspace/Tab;
  - CSI arrows;
  - SS3 arrows;
  - xterm modifyOtherKeys;
  - kitty key protocol;
  - bracketed paste;
  - partial escape sequences;
  - lone Escape timeout.

### Linux Integration Tests

Use a pseudo-terminal harness:

- start TUI under PTY;
- send `g`, assert model picker filters;
- send `ESC[B`, assert selection moves;
- send `ESC OB`, assert application cursor Down moves;
- send `Enter`, assert model selected or API prompt opens;
- send bracketed paste into API prompt;
- verify process restores terminal mode after exit.

### Manual Smoke Matrix

Linux:

- Kitty;
- Ghostty;
- GNOME Terminal / VTE if available;
- tmux inside one terminal, if feasible.

Windows:

- Windows Terminal;
- classic console only if supported.

macOS:

- Terminal.app;
- iTerm2;
- Ghostty/Kitty if available.

## 12. Risks

| Risk | Mitigation |
| --- | --- |
| Incorrect native struct corrupts terminal state | Separate per-OS structs; tests; compare with known projects. |
| Crash leaves terminal raw | Emergency restore; scoped restore; avoid changing fd flags unless necessary. |
| `poll()` P/Invoke varies by platform | Separate Linux/macOS native files. |
| Windows behavior regresses | Move code first, redesign later. |
| Parser regressions | Extract parser with tests before backend rewrite. |

## 13. Acceptance Criteria

- TUI no longer constructs a mixed `SystemTerminalBackend` directly.
- Linux backend uses `termios` + `poll()` + `read()` and no `.NET Console` input APIs.
- Linux model picker supports typing, Enter, Escape, CSI arrows, and SS3 arrows.
- API key prompt supports typed and bracketed-paste input.
- Windows backend still builds and preserves existing behavior.
- macOS code has a separate termios layout and does not use Linux constants.
- `dotnet build` and existing tests pass.
