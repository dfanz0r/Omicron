using System.Text;
using Omicron.Core.Rendering;
using Omicron.Core.Rendering.Layout;
using Omicron.Core.Text;

namespace Omicron.CLI.Tui;

/// <summary>
/// A modal dialog that prompts the user for an API key inside the TUI.
/// Input is masked with bullet characters.
/// </summary>
public sealed class TuiApiKeyPrompt : ITuiWidget
{
    private readonly StringBuilder _input = new();
    private bool _completed;
    private bool _cancelled;
    private bool _showKey;
    private Rect _bounds;
    private string _providerName = "";

    /// <summary>The provider name being prompted for.</summary>
    public string ProviderName => _providerName;

    /// <summary>Whether input is complete.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Whether the prompt was cancelled.</summary>
    public bool IsCancelled => _cancelled;

    /// <summary>The entered API key (empty if cancelled).</summary>
    public string ApiKey => _input.ToString();

    /// <summary>Reset the prompt for a new provider.</summary>
    public void Reset(string providerName)
    {
        _providerName = providerName;
        _input.Clear();
        _completed = false;
        _cancelled = false;
    }

    /// <summary>Handle a key event. Returns true if consumed.</summary>
    public bool HandleKey(KeyEvent ke)
    {
        if (_completed) return false;

        switch (ke.Key)
        {
            case Key.Character when ke.Text.HasValue:
                char c = (char)ke.Text.Value.Value;
                if (c >= 32 && c != 127)
                {
                    _input.Append(c);
                    return true;
                }
                return false;

            case Key.Backspace:
                if (_input.Length > 0)
                {
                    _input.Length--;
                    return true;
                }
                return false;

            case Key.Character when ke.Text.HasValue && (char)ke.Text.Value.Value == '\u0013': // Ctrl+S → toggle show key
                _showKey = !_showKey;
                return true;

            case Key.Enter:
                _completed = true;
                return true;

            case Key.Escape:
                _completed = true;
                _cancelled = true;
                return true;
        }

        return false;
    }

    // ── ITuiWidget implementation ──

    public Size Measure(Size available) => available;

    public void Arrange(Rect bounds)
    {
        _bounds = bounds;
    }

    public void Render(RenderContext context)
    {
        // Calculate dialog dimensions
        int dialogWidth = Math.Min(60, _bounds.Width - 4);
        int dialogHeight = 5;
        int dialogX = _bounds.X + (_bounds.Width - dialogWidth) / 2;
        int dialogY = _bounds.Y + (_bounds.Height - dialogHeight) / 2;

        if (dialogX < 0) dialogX = 0;
        if (dialogY < 0) dialogY = 0;

        // Dim background
        var dimCell = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = TextStyle.Default,
        };
        context.FillRect(_bounds, dimCell);

        // Draw dialog background
        var dialogBg = new RenderCell
        {
            Glyph = GlyphRef.Ascii((byte)' '),
            Width = 1,
            Style = new TextStyle(200, 200, 200, 30, 30, 30, false, false, false), // dark bg, light fg
        };
        context.FillRect(new Rect(dialogX, dialogY, dialogWidth, dialogHeight), dialogBg);

        // Title
        string title = $" Enter API key for {_providerName}: ";
        context.DrawText(dialogX + 1, dialogY + 1,
            Encoding.UTF8.GetBytes(title),
            TextStyle.Default);

        // Masked input with cursor positioned by display width
        string displayText = _showKey ? _input.ToString() : new string('\u2022', _input.Length);
        string inputLine = $" > {displayText}";
        int cursorCol = dialogX + 3 + GetDisplayWidth(displayText);
        context.DrawText(dialogX + 1, dialogY + 2,
            Encoding.UTF8.GetBytes(inputLine),
            TextStyle.Default);

        // Cursor (blinking block)
        if (cursorCol < dialogX + dialogWidth - 1)
        {
            context.DrawText(cursorCol, dialogY + 2,
                Encoding.UTF8.GetBytes(" "),
                TextStyle.Inverted);
        }

        // Hint
        string hint = _showKey
            ? " Enter to confirm  |  Escape to cancel  |  Ctrl+S to hide "
            : " Enter to confirm  |  Escape to cancel  |  Ctrl+S to show ";
        context.DrawText(dialogX + 1, dialogY + 3,
            Encoding.UTF8.GetBytes(hint),
            TextStyle.ForegroundOnly(150, 150, 150));
    }

    private static int GetDisplayWidth(string text)
    {
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += CellWidthCalculator.GetWidth(rune);
        return width;
    }
}
