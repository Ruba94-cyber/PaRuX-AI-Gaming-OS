using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Services
{
    /// <summary>
    /// An in-memory proposed result, NOT a file mutation or permission to write it.
    /// A real executor must compare the preimage hash again, check workspace containment
    /// and perform the existing approved transactional apply.
    /// </summary>
    public sealed record CodingHunkPreviewResult(
        bool Ready, string? CandidateText, string PreimageSha256,
        string? CandidateSha256, string Explanation);

    public static class CodingSelectiveHunkPreview
    {
        private const int MaximumSourceCharacters = 2_000_000;
        private const int MaximumHunks = 500;

        public static CodingHunkPreviewResult Create(
            string relativePath, string originalText,
            IReadOnlyList<CodingReviewHunk> hunks,
            IReadOnlyList<CodingReviewChoice> decisions)
        {
            ArgumentNullException.ThrowIfNull(originalText);
            ArgumentNullException.ThrowIfNull(hunks);
            ArgumentNullException.ThrowIfNull(decisions);

            string preimage = Hash(originalText);
            CodingHunkPreviewResult Refuse(string message) => new(false, null, preimage, null, message);

            if (!SafePath(relativePath))
                return Refuse("Select one safe workspace-relative file path.");
            if (originalText.Length > MaximumSourceCharacters || hunks.Count > MaximumHunks)
                return Refuse("File or diff is too large for a selective preview.");
            if (originalText.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\r'))
                return Refuse("Mixed or unsupported line endings need manual review.");

            CodingHunkApplyPlan plan = CodingHunkReviewPlanner.Plan(hunks, decisions);
            if (!plan.Ready) return Refuse(plan.Explanation);
            if (hunks.Any(h => !string.Equals(h.RelativePath, relativePath, StringComparison.Ordinal)))
                return Refuse("A selective preview must contain changes for exactly one file.");

            bool trailingNewline = originalText.EndsWith("\n", StringComparison.Ordinal);
            string newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string normalized = originalText.Replace("\r\n", "\n", StringComparison.Ordinal);
            var originalLines = normalized.Length == 0
                ? new List<string>() : normalized.Split('\n').ToList();
            if (trailingNewline) originalLines.RemoveAt(originalLines.Count - 1);

            var output = new List<string>();
            var accepted = new HashSet<string>(plan.Accepted.Select(h => h.Id), StringComparer.Ordinal);
            int cursor = 0;
            foreach (CodingReviewHunk hunk in hunks)
            {
                if (hunk.OldStart < 0 || hunk.OldCount < 0 || hunk.NewCount < 0
                    || hunk.Text.Contains("\\ No newline at end of file", StringComparison.Ordinal))
                    return Refuse("Unsupported hunk or missing final-newline marker.");
                var expected = new List<string>();
                var replacement = new List<string>();
                foreach (string line in hunk.Text.Split('\n'))
                {
                    if (line.Length == 0) return Refuse("Malformed hunk body.");
                    string text = line.Substring(1);
                    switch (line[0])
                    {
                        case ' ': expected.Add(text); replacement.Add(text); break;
                        case '-': expected.Add(text); break;
                        case '+': replacement.Add(text); break;
                        default: return Refuse("Unsupported diff operation.");
                    }
                }
                if (expected.Count != hunk.OldCount || replacement.Count != hunk.NewCount)
                    return Refuse("Hunk line counts are inconsistent.");
                int start = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
                if (start < cursor || start > originalLines.Count
                    || hunk.OldCount > originalLines.Count - start)
                    return Refuse("Overlapping or out-of-range hunks.");
                for (int j = 0; j < expected.Count; j++)
                    if (!string.Equals(originalLines[start + j], expected[j], StringComparison.Ordinal))
                        return Refuse("File changed since the diff was prepared; refresh the review.");

                output.AddRange(originalLines.Skip(cursor).Take(start - cursor));
                output.AddRange(accepted.Contains(hunk.Id) ? replacement : expected);
                cursor = start + hunk.OldCount;
            }
            output.AddRange(originalLines.Skip(cursor));

            string candidate = output.Count == 0
                ? string.Empty
                : string.Join(newline, output) + (trailingNewline ? newline : string.Empty);
            if (candidate.Length > MaximumSourceCharacters * 2)
                return Refuse("Result exceeds the preview size limit.");
            return new(true, candidate, preimage, Hash(candidate),
                "Preview only. Re-check the source hash and approval before a transactional write.");
        }

        public static bool PreimageMatches(string? expectedSha256, string? actualText) =>
            !string.IsNullOrEmpty(actualText) && expectedSha256 is { Length: 64 }
            && string.Equals(expectedSha256, Hash(actualText), StringComparison.OrdinalIgnoreCase);

        private static string Hash(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static bool SafePath(string? path) =>
            !string.IsNullOrWhiteSpace(path) && path.Length <= 512
            && !path.StartsWith("/", StringComparison.Ordinal)
            && !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0')
            && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }
}
