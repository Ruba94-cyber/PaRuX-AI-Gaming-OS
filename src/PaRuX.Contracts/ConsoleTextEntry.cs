namespace PaRuX.Contracts;

/// <summary>
/// Pure text-editing rules for the Console on-screen keyboard. The WinUI layer only renders keys and
/// forwards presses here, so caret handling, length limits and the accepted character set are
/// testable without a window and identical for every text field that uses the keyboard.
/// </summary>
public static class ConsoleTextEntry
{
    public const int MaxLength = 80;

    /// <summary>Key rows in display order. Letters are lower case; the view may show them upper case.</summary>
    public static readonly IReadOnlyList<string> Rows = new[]
    {
        "1234567890",
        "qwertyuiop",
        "asdfghjkl-",
        "zxcvbnm._'",
    };

    public static bool IsAllowedKey(string key) =>
        key.Length == 1 && (char.IsLetterOrDigit(key[0]) || key[0] is ' ' or '.' or '-' or '_' or '\'');

    public static (string Text, int Caret) Insert(string? text, int caret, string key)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        if (!IsAllowedKey(key) || text.Length >= MaxLength) return (text, caret);
        return (text.Insert(caret, key), caret + 1);
    }

    public static (string Text, int Caret) Backspace(string? text, int caret)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        return caret == 0 ? (text, 0) : (text.Remove(caret - 1, 1), caret - 1);
    }

    public static (string Text, int Caret) Clear(string? _, int __) => (string.Empty, 0);
}
