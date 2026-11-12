using System;
using System.Globalization;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure English/Italian text selection shared by code-built views and the XAML hook (<c>PaRuX.Views.Loc</c>).
    /// English is the source text and the fallback; Italian wins only when it is selected and not empty.
    /// The language comes from <see cref="UiLanguage"/> (it sets the process UI culture), so a restart-time
    /// switch needs no per-element refresh.
    /// </summary>
    public static class UiText
    {
        public static bool IsItalian =>
            string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, UiLanguage.Italian, StringComparison.OrdinalIgnoreCase);

        public static string Pick(string en, string? it) => Pick(en, it, IsItalian);

        public static string Pick(string en, string? it, bool italian) =>
            italian && !string.IsNullOrWhiteSpace(it) ? it! : en;
    }
}
