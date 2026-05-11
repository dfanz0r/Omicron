using System.Text;

namespace Omicron.Core.Rendering;

/// <summary>Abstract base for all terminal events.</summary>
public abstract record TerminalEvent;

/// <summary>A key press or release event.</summary>
/// <param name="Key">The key that was pressed.</param>
/// <param name="Modifiers">Active modifier keys.</param>
/// <param name="Text">For Key.Character, the Unicode scalar value of the typed character.</param>
public sealed record KeyEvent(Key Key, KeyModifiers Modifiers, Rune? Text) : TerminalEvent;

/// <summary>A mouse button event (press, release, move, drag).</summary>
public sealed record MouseEvent(
    int Row, int Column,
    MouseButton Button,
    MouseEventKind Kind,
    KeyModifiers Modifiers) : TerminalEvent;

/// <summary>Terminal resize event.</summary>
public sealed record ResizeEvent(int Width, int Height) : TerminalEvent;

// ── Enums ──

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
