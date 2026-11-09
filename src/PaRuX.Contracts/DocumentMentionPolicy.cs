using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Contracts;

/// <summary>
/// "#collection" mentions in the chat box: typing <c>#manuals what is the torque spec?</c> asks only the document folder
/// named "Manuals" (like a workspace/collection in other document assistants). Pure and side-effect free.
/// A collection is a registered document folder; its mention name is the folder's own name, lower-cased, with every run of
/// characters other than letters/digits collapsed to a single dash. A unique prefix also matches.
/// </summary>
public static class DocumentMentionPolicy
{
    public sealed record Result(string CleanText, IReadOnlyList<string> SourceIds, IReadOnlyList<string> Unmatched);

    private static readonly Regex Tag = new(@"(?<![\p{L}\p{N}_&/])#([\p{L}\p{N}][\p{L}\p{N}_\-]{0,63})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>The mention name of a folder, e.g. <c>C:\Docs\Service Manuals 2025</c> becomes <c>service-manuals-2025</c>.</summary>
    public static string Slug(string? folderPath)
    {
        string name = Path.GetFileName((folderPath ?? string.Empty).TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = folderPath ?? string.Empty;
        var sb = new StringBuilder();
        bool dash = false;
        foreach (char c in name.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Finds mentions and the exact folders they name. Tags that match nothing are left in the text (they may just be a
    /// hashtag the person meant literally) and reported in <see cref="Result.Unmatched"/>.
    /// </summary>
    public static Result Resolve(string? text, IReadOnlyList<(string Id, string FolderPath)> sources)
    {
        string input = text ?? string.Empty;
        if (input.IndexOf('#') < 0 || sources.Count == 0)
            return new Result(input, Array.Empty<string>(), Array.Empty<string>());

        var slugs = sources.Select(s => (s.Id, Slug: Slug(s.FolderPath))).Where(s => s.Slug.Length > 0).ToList();
        var matchedIds = new List<string>();
        var unmatched = new List<string>();
        string cleaned;
        try
        {
            cleaned = Tag.Replace(input, match =>
            {
                string tag = Slug(match.Groups[1].Value);
                var exact = slugs.Where(s => s.Slug == tag).ToList();
                var chosen = exact.Count > 0 ? exact : slugs.Where(s => s.Slug.StartsWith(tag, StringComparison.Ordinal)).ToList();
                // A prefix that fits several folders is ambiguous: never guess which one the person meant.
                if (chosen.Count == 0 || (exact.Count == 0 && chosen.Count > 1))
                {
                    unmatched.Add(match.Groups[1].Value);
                    return match.Value;
                }
                foreach (var c in chosen) if (!matchedIds.Contains(c.Id)) matchedIds.Add(c.Id);
                return string.Empty;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return new Result(input, Array.Empty<string>(), Array.Empty<string>());
        }

        if (matchedIds.Count == 0) return new Result(input, Array.Empty<string>(), unmatched);
        cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ").Trim();
        // The whole message was only mentions: keep the original so there is still a question to send.
        return new Result(cleaned.Length == 0 ? input : cleaned, matchedIds, unmatched);
    }
}
