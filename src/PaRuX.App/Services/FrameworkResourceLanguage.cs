using System;
using System.Globalization;

namespace PaRuX.Services
{
    /// <summary>
    /// WinUI framework strings (navigation pane toggle name, ComboBox popup close, ...) are resolved by MRT from the process
    /// resource language, which defaults to the Windows display language and ignores <see cref="CultureInfo.CurrentUICulture"/>.
    /// Overriding the primary language before the first window is created makes those strings follow the app's selected
    /// language, so one screen is never half Italian and half English. A failure only leaves framework strings in the OS
    /// language; the app's own strings still follow the selection.
    /// </summary>
    internal static class FrameworkResourceLanguage
    {
        public static void Apply(string? savedUiLanguage)
        {
            string bcp47 = UiLanguage.Normalize(savedUiLanguage) == UiLanguage.Italian ? "it-IT" : "en-US";
            try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = bcp47; }
            catch (Exception ex) { Log.Write("Framework resource language override failed: " + ex.Message); }
        }
    }
}
