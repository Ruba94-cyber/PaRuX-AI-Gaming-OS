using System;
using System.Globalization;

namespace PaRuX.Services
{
    /// <summary>
    /// The interface language. English is the product default and does NOT follow the Windows display language
    /// (owner decision 2026-10-21: English base, Italian optional). Italian is an explicit choice in
    /// Settings; the many "is the UI Italian" checks read <see cref="CultureInfo.CurrentUICulture"/>,
    /// so applying the choice to the culture is enough to switch them all.
    /// </summary>
    public static class UiLanguage
    {
        public const string English = "en";
        public const string Italian = "it";

        public static string Normalize(string? value) =>
            string.Equals(value?.Trim(), Italian, StringComparison.OrdinalIgnoreCase) ? Italian : English;

        /// <summary>Makes the process-wide UI culture match the saved choice. Safe to call repeatedly.</summary>
        public static void Apply(string? saved)
        {
            var culture = CultureInfo.GetCultureInfo(Normalize(saved) == Italian ? "it-IT" : "en-US");
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }
}
