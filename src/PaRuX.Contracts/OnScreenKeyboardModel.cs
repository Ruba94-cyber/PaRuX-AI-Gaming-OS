namespace PaRuX.Contracts;

public enum OnScreenKeyboardLayer { Letters, Symbols }

public enum OnScreenKeyboardPadAction { None, Done, Cancel, Backspace, Space, CaretLeft, CaretRight, ToggleShift, ToggleLayer }

/// <summary>One key. <see cref="Id"/> is stable (used for AutomationId); <see cref="Label"/> already reflects shift/caps.</summary>
public sealed record OnScreenKey(string Id, string Label, string Output);

/// <summary>
/// Pure typing logic for the Console/TV on-screen keyboard: layers, one-shot Shift, Caps, caret edits and the
/// (optionally masked) preview. No WinUI types, no input hooks, no I/O, so it is fully unit-testable. The view only
/// renders <see cref="GetRows"/> and forwards presses here.
/// </summary>
public sealed class OnScreenKeyboardModel
{
    public const int DefaultMaxLength = 100_000;
    public const char MaskChar = '•';

    private static readonly string[] LetterRows = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
    private const string DigitRow = "1234567890";
    private const string AccentRowChars = "àèéìòùç";

    private static readonly (string Id, string Ch)[][] SymbolRows =
    {
        new[] { ("excl", "!"), ("at", "@"), ("hash", "#"), ("dollar", "$"), ("percent", "%"), ("caret", "^"), ("amp", "&"), ("star", "*"), ("lparen", "("), ("rparen", ")") },
        new[] { ("minus", "-"), ("underscore", "_"), ("plus", "+"), ("equals", "="), ("lbracket", "["), ("rbracket", "]"), ("lbrace", "{"), ("rbrace", "}"), ("pipe", "|"), ("backslash", "\\") },
        new[] { ("colon", ":"), ("semicolon", ";"), ("quote", "\""), ("apostrophe", "'"), ("comma", ","), ("period", "."), ("question", "?"), ("slash", "/"), ("lt", "<"), ("gt", ">") },
        new[] { ("tilde", "~"), ("backtick", "`"), ("euro", "€"), ("pound", "£") },
    };

    private string _text;

    public OnScreenKeyboardModel(string? text, int caret, bool isPassword = false, int maxLength = DefaultMaxLength,
        bool showAccentRow = false)
    {
        _text = text ?? string.Empty;
        Caret = Math.Clamp(caret, 0, _text.Length);
        InitialText = _text;
        InitialCaret = Caret;
        IsPassword = isPassword;
        MaxLength = Math.Max(1, maxLength);
        ShowAccentRow = showAccentRow;
    }

    public string Text => _text;
    public int Caret { get; private set; }
    public bool IsPassword { get; }
    public int MaxLength { get; }
    public bool ShowAccentRow { get; }
    public string InitialText { get; }
    public int InitialCaret { get; }
    public OnScreenKeyboardLayer Layer { get; private set; } = OnScreenKeyboardLayer.Letters;
    /// <summary>One-shot: applies to the next typed key, then clears.</summary>
    public bool ShiftActive { get; private set; }
    public bool CapsLock { get; private set; }
    public bool UpperCase => ShiftActive ^ CapsLock;

    /// <summary>Text shown in the keyboard preview. A password is never shown in clear.</summary>
    public string Preview => IsPassword ? new string(MaskChar, _text.Length) : _text;

    /// <summary>Preview with a caret marker for rendering.</summary>
    public string PreviewWithCaret => Preview.Insert(Math.Clamp(Caret, 0, Preview.Length), "|");

    public IReadOnlyList<IReadOnlyList<OnScreenKey>> GetRows()
    {
        var rows = new List<IReadOnlyList<OnScreenKey>>();
        if (Layer == OnScreenKeyboardLayer.Letters)
        {
            rows.Add(DigitRow.Select(c => new OnScreenKey("d" + c, c.ToString(), c.ToString())).ToArray());
            foreach (string row in LetterRows)
                rows.Add(row.Select(c => Letter(c, null)).ToArray());
            if (ShowAccentRow)
                rows.Add(AccentRowChars.Select(c => Letter(c, "acc_" + (int)c)).ToArray());
        }
        else
        {
            foreach (var row in SymbolRows)
                rows.Add(row.Select(k => new OnScreenKey("s_" + k.Id, k.Ch, k.Ch)).ToArray());
        }
        return rows;
    }

    private OnScreenKey Letter(char c, string? id)
    {
        string s = UpperCase ? char.ToUpperInvariant(c).ToString() : c.ToString();
        return new OnScreenKey(id ?? "k_" + c, s, s);
    }

    /// <summary>Inserts one key's output at the caret. Returns false when rejected (control chars, length limit).</summary>
    public bool Type(string output)
    {
        if (string.IsNullOrEmpty(output) || output.Any(char.IsControl)) return false;
        if (_text.Length + output.Length > MaxLength) return false;
        _text = _text.Insert(Caret, output);
        Caret += output.Length;
        ShiftActive = false;
        return true;
    }

    public bool Space() => Type(" ");

    public bool Backspace()
    {
        if (Caret == 0) return false;
        _text = _text.Remove(Caret - 1, 1);
        Caret--;
        return true;
    }

    public void Clear() { _text = string.Empty; Caret = 0; }

    public void MoveCaret(int delta) => Caret = Math.Clamp(Caret + delta, 0, _text.Length);

    public void ToggleShift() => ShiftActive = !ShiftActive;

    public void ToggleCaps() { CapsLock = !CapsLock; ShiftActive = false; }

    public void SetLayer(OnScreenKeyboardLayer layer) { Layer = layer; ShiftActive = false; }

    public void ToggleLayer() =>
        SetLayer(Layer == OnScreenKeyboardLayer.Letters ? OnScreenKeyboardLayer.Symbols : OnScreenKeyboardLayer.Letters);

    /// <summary>VirtualKey codes of the Windows SDK, kept assembly-free like ConsoleNavigationPolicy.
    /// B/Escape cancel; Start (Menu) done; X backspace; Y space; LB/RB move the caret; LT shift; RT layer.</summary>
    public static OnScreenKeyboardPadAction ResolvePad(int virtualKey) => virtualKey switch
    {
        27 or 196 => OnScreenKeyboardPadAction.Cancel,
        207 => OnScreenKeyboardPadAction.Done,
        197 => OnScreenKeyboardPadAction.Backspace,
        198 => OnScreenKeyboardPadAction.Space,
        200 => OnScreenKeyboardPadAction.CaretLeft,
        199 => OnScreenKeyboardPadAction.CaretRight,
        201 => OnScreenKeyboardPadAction.ToggleShift,
        202 => OnScreenKeyboardPadAction.ToggleLayer,
        _ => OnScreenKeyboardPadAction.None
    };

    /// <summary>Applies an edit/navigation action; Done and Cancel are the host's job. Returns true when state changed.</summary>
    public bool Apply(OnScreenKeyboardPadAction action)
    {
        switch (action)
        {
            case OnScreenKeyboardPadAction.Backspace: return Backspace();
            case OnScreenKeyboardPadAction.Space: return Space();
            case OnScreenKeyboardPadAction.CaretLeft: MoveCaret(-1); return true;
            case OnScreenKeyboardPadAction.CaretRight: MoveCaret(1); return true;
            case OnScreenKeyboardPadAction.ToggleShift: ToggleShift(); return true;
            case OnScreenKeyboardPadAction.ToggleLayer: ToggleLayer(); return true;
            default: return false;
        }
    }

    /// <summary>Auto-open only in Console mode, only when the focus move came from a gamepad key (not a physical
    /// keyboard or pointer), only for editable fields and never while already open.</summary>
    public static bool ShouldAutoOpen(bool consoleModeActive, bool lastInputWasGamepad, bool isEditable, bool alreadyOpen) =>
        consoleModeActive && lastInputWasGamepad && isEditable && !alreadyOpen;

    /// <summary>True for the Windows gamepad virtual keys (GamepadA .. RightThumbstickLeft).</summary>
    public static bool IsGamepadKey(int virtualKey) => virtualKey is >= 195 and <= 218;
}
