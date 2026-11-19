using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>Pure filtering used by the palette (and its tests): every typed word must start a word of the title or keywords.</summary>
    public static class CommandPaletteFilter
    {
        public static IReadOnlyList<T> Filter<T>(IEnumerable<T> items, string? query, Func<T, string> title, Func<T, string> keywords)
        {
            string[] words = (query ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return items.ToArray();
            return items
                .Select(item => (item, haystack: Tokens(title(item) + " " + keywords(item)), titleTokens: Tokens(title(item))))
                .Where(x => words.All(w => x.haystack.Any(t => t.StartsWith(w, StringComparison.CurrentCultureIgnoreCase))))
                // Title hits first, then keyword-only hits.
                .OrderByDescending(x => words.All(w => x.titleTokens.Any(t => t.StartsWith(w, StringComparison.CurrentCultureIgnoreCase))))
                .Select(x => x.item)
                .ToArray();
        }

        private static string[] Tokens(string text) =>
            text.Split(new[] { ' ', '\t', '-', '/', '&', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
