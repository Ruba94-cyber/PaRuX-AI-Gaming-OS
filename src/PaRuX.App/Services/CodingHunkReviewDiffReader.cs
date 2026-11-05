using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only, tracked-text-file diff retrieval from the existing trusted
    /// coding task worktree. This does not use git add, checkout, apply, hooks,
    /// external diff tools, textconv, commits or any mutation of the user tree.
    /// Git stdout is bounded after capture by the existing host Git runner.
    /// </summary>
    public sealed record CodingHunkDiffRead(
        bool Ready, string Explanation, string? Diff,
        IReadOnlyList<CodingReviewHunk> Hunks);

    public static class CodingHunkReviewDiffReader
    {
        public const int MaximumFiles = 100;
        private const int MaximumDiffLength = 2_000_000;

        public static IReadOnlyList<string>? ParsePathList(string? nameOnlyZ)
        {
            if (nameOnlyZ is null || nameOnlyZ.Length > 65536)
                return null;
            if (nameOnlyZ.Length == 0) return Array.Empty<string>();
            if (nameOnlyZ[^1] != '\0') return null;
            string[] paths = nameOnlyZ.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length > MaximumFiles || paths.Distinct(StringComparer.Ordinal).Count() != paths.Length
                || paths.Any(p => !SafePath(p)))
                return null;
            return paths;
        }

        public static async Task<IReadOnlyList<string>?> ChangedPathsAsync(
            string worktree, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree))
                return null;
            (int exit, string listing) = await CodingAgentTools.RunGitDiffAsync(
                worktree, ct, "--name-only", "-z", "--no-renames", "HEAD", "--")
                .ConfigureAwait(false);
            return exit == 0 ? ParsePathList(listing) : null;
        }

        public static async Task<CodingHunkDiffRead> ReadAsync(
            string worktree, string relativePath, CancellationToken ct)
        {
            CodingHunkDiffRead Refuse(string why) =>
                new(false, why, null, Array.Empty<CodingReviewHunk>());
            if (!SafePath(relativePath))
                return Refuse("Select one safe, tracked file in the coding task worktree.");
            IReadOnlyList<string>? changed = await ChangedPathsAsync(worktree, ct)
                .ConfigureAwait(false);
            if (changed is null)
                return Refuse("Could not obtain a complete bounded list of changed tracked files.");
            if (!changed.Contains(relativePath, StringComparer.Ordinal))
                return Refuse("The file is not a current tracked change in this task.");
            (int exit, string diff) = await CodingAgentTools.RunGitDiffAsync(
                worktree, ct, "--no-renames", "--unified=3", "HEAD", "--", relativePath)
                .ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(diff) || diff.Length > MaximumDiffLength)
                return Refuse("The selected file has no bounded text diff.");
            try
            {
                IReadOnlyList<CodingReviewHunk> hunks = CodingHunkReviewPlanner.Parse(diff);
                if (hunks.Count == 0 || hunks.Any(h => !string.Equals(h.RelativePath,
                    relativePath, StringComparison.Ordinal)))
                    return Refuse("The Git diff contains unsupported changes or another file.");
                return new(true, "Review every changed block before continuing.", diff, hunks);
            }
            catch (FormatException)
            {
                return Refuse("This file has a binary, renamed, new/deleted or unsupported diff; use full-file review.");
            }
        }

        public static bool SafePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 512
                || path.StartsWith("/", StringComparison.Ordinal)
                || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
                return false;
            return path.Split('/').All(part => part.Length > 0
                && part is not ("." or "..")
                && !part.Any(char.IsWhiteSpace)
                && !string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase));
        }
    }
}
