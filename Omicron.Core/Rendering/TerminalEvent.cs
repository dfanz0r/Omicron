using System.Text;

namespace Omicron.Core.Rendering;

/// <summary>Abstract base for all terminal events.</summary>
public abstract record TerminalEvent;

/// <summary>A key press or release event.</summary>
/// <param name="Key">The key that was pressed.</param>
/// <param name="Modifiers">Active modifier keys.</param>
/// <param name="Text">For Key.Character, the Unicode scalar value of the typed character.</param>
public sealed record KeyEvent(Key Key, KeyModifiers Modifiers, Rune? Text) : TerminalEvent
{
    /// <summary>
    /// The raw key code from the kitty protocol (Unicode codepoint of the
    /// un-shifted key). Null for legacy events that didn't come through
    /// the kitty protocol.
    /// </summary>
    public int? KeyCode { get; init; }

    /// <summary>
    /// The text that would be produced by this key combination in the
    /// current keyboard layout. Computed from KeyCode + Modifiers when
    /// the kitty protocol is active. Null when unknown.
    /// </summary>
    public string? ResolvedText { get; init; }

    /// <summary>
    /// Event type for kitty protocol (press/repeat/release). Always
    /// <see cref="KeyEventType.Press"/> for legacy terminals.
    /// </summary>
    public KeyEventType EventType { get; init; } = KeyEventType.Press;
}

/// <summary>A mouse button event (press, release, move, drag).</summary>
public sealed record MouseEvent(
    int Row, int Column,
    MouseButton Button,
    MouseEventKind Kind,
    KeyModifiers Modifiers) : TerminalEvent;

/// <summary>Terminal resize event.</summary>
public sealed record ResizeEvent(int Width, int Height) : TerminalEvent;

/// <summary>Bracketed paste event containing pasted text.</summary>
/// <param name="Text">The pasted text content.</param>
public sealed record PasteEvent(string Text) : TerminalEvent;

// ── Enums ──

/// <summary>Event type for kitty keyboard protocol (press/repeat/release).</summary>
public enum KeyEventType
{
    /// <summary>Key pressed down.</summary>
    Press,
    /// <summary>Key held down (auto-repeat).</summary>
    Repeat,
    /// <summary>Key released.</summary>
    Release,
}

/// <summary>Keys that can be pressed.</summary>
public enum Key
{
    None,
    Enter,
    Escape,
    Backspace,
    Tab,
    Space,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Delete,
    Insert,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
    F21,
    F22,
    F23,
    F24,
    /// <summary>A printable character; the actual value is in <see cref="KeyEvent.Text"/>.</summary>
    Character,
}

/// <summary>Key modifier flags.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

/// <summary>Mouse buttons.</summary>
public enum MouseButton
{
    None,
    Left,
    Middle,
    Right,
    ScrollUp,
    ScrollDown,
}

/// <summary>Kind of mouse event.</summary>
public enum MouseEventKind
{
    Pressed,
    Released,
    Moved,
    Dragged,
}
