using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    public enum CodingHunkDecision { Pending, Accept, Reject }

    /// <summary>A display-only hunk. Its content is untrusted diff text, never an executable patch.</summary>
    public sealed record CodingReviewHunk(
        string Id, string RelativePath, int OldStart, int OldCount,
        int NewStart, int NewCount, string Header, string Text);

    public sealed record CodingReviewChoice(string HunkId, CodingHunkDecision Decision);

    public sealed record CodingHunkApplyPlan(
        IReadOnlyList<CodingReviewHunk> Accepted,
        IReadOnlyList<CodingReviewHunk> Rejected,
        bool Ready, string Explanation);

    /// <summary>
    /// Per-hunk review data for the existing CodingTaskReviewModel workflow.
    /// No file is modified: a future executor must re-read its trusted preimage,
    /// validate paths, acquire the one-shot approval and apply transactionally.
    /// </summary>
    public static class CodingHunkReviewPlanner
    {
        private static readonly Regex Header = new(
            @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static IReadOnlyList<CodingReviewHunk> Parse(string? unifiedDiff)
        {
            if (string.IsNullOrWhiteSpace(unifiedDiff)) return Array.Empty<CodingReviewHunk>();
            if (unifiedDiff.Length > 2_000_000)
                throw new ArgumentOutOfRangeException(nameof(unifiedDiff), "Diff preview too large.");

            string[] lines = unifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var result = new List<CodingReviewHunk>();
            var path = string.Empty;
            var oldPath = string.Empty;
            bool insideDiff = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    insideDiff = true;
                    path = string.Empty;
                    oldPath = string.Empty; // Never carry identity into the next file.
                    continue;
                }
                if (line.StartsWith("--- a/", StringComparison.Ordinal))
                {
                    string candidate = line.Substring(6);
                    oldPath = insideDiff && IsSafeRelativePath(candidate) ? candidate : string.Empty;
                    continue;
                }
                if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                {
                    string candidate = line.Substring(6);
                    path = insideDiff && IsSafeRelativePath(candidate)
                        && string.Equals(candidate, oldPath, StringComparison.Ordinal)
                        ? candidate : string.Empty;
                    continue;
                }
                if (!line.StartsWith("@@ ", StringComparison.Ordinal)) continue;

                Match match = Header.Match(line);
                if (!match.Success || path.Length == 0)
                    throw new FormatException("Unsupported or unscoped diff hunk.");
                string oldCountText = match.Groups[2].Success ? match.Groups[2].Value : "1";
                string newCountText = match.Groups[4].Success ? match.Groups[4].Value : "1";
                if (!int.TryParse(match.Groups[1].Value, out int oldStart)
                    || !int.TryParse(oldCountText, out int oldCount)
                    || !int.TryParse(match.Groups[3].Value, out int newStart)
                    || !int.TryParse(newCountText, out int newCount))
                    throw new FormatException("Diff hunk line number is out of range.");

                var body = new List<string>();
                int oldLines = 0, newLines = 0;
                while (i + 1 < lines.Length)
                {
                    string next = lines[i + 1];
                    // A deleted line can start "--- " and an added line can start "+++ ".
                    // Treat them as body until the declared counts are satisfied.
                    if (oldLines == oldCount && newLines == newCount)
                    {
                        if (next.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                        {
                            body.Add(next);
                            i++;
                            continue;
                        }
                        if (next.Length == 0 || next.StartsWith("@@ ", StringComparison.Ordinal)
                            || next.StartsWith("diff --git ", StringComparison.Ordinal)
                            || next.StartsWith("--- ", StringComparison.Ordinal)
                            || next.StartsWith("+++ ", StringComparison.Ordinal)) break;
                        throw new FormatException("Unexpected diff content after hunk line counts.");
                    }
                    if (next.Length == 0) throw new FormatException("Incomplete diff hunk.");
                    if (next[0] == ' ') { oldLines++; newLines++; }
                    else if (next[0] == '-') oldLines++;
                    else if (next[0] == '+') newLines++;
                    else throw new FormatException("Unsupported diff body.");
                    if (oldLines > oldCount || newLines > newCount)
                        throw new FormatException("Diff body exceeds its declared line counts.");
                    body.Add(next);
                    i++;
                }
                if (oldLines != oldCount || newLines != newCount)
                    throw new FormatException("Diff hunk line counts do not match header.");
                string id = path + ":" + oldStart + ":" + newStart + ":" + result.Count;
                result.Add(new CodingReviewHunk(id, path, oldStart, oldCount, newStart, newCount,
                    line, string.Join("\n", body)));
            }
            return result;
        }

        public static CodingHunkApplyPlan Plan(
            IReadOnlyList<CodingReviewHunk> hunks, IReadOnlyList<CodingReviewChoice> choices)
        {
            ArgumentNullException.ThrowIfNull(hunks);
            ArgumentNullException.ThrowIfNull(choices);
            var ids = new HashSet<string>(hunks.Select(h => h.Id), StringComparer.Ordinal);
            if (ids.Count != hunks.Count || choices.Count != hunks.Count
                || choices.Any(c => !ids.Contains(c.HunkId))
                || choices.Select(c => c.HunkId).Distinct(StringComparer.Ordinal).Count() != choices.Count)
                return new(Array.Empty<CodingReviewHunk>(), Array.Empty<CodingReviewHunk>(),
                    false, "Every hunk requires exactly one matching review decision.");
            var selected = choices.ToDictionary(c => c.HunkId, c => c.Decision, StringComparer.Ordinal);
            if (selected.Values.Any(v => v == CodingHunkDecision.Pending)
                || selected.Values.Any(v => !Enum.IsDefined(typeof(CodingHunkDecision), v)))
                return new(Array.Empty<CodingReviewHunk>(), Array.Empty<CodingReviewHunk>(),
                    false, "Resolve every hunk before applying.");
            var accepted = hunks.Where(h => selected[h.Id] == CodingHunkDecision.Accept).ToArray();
            var rejected = hunks.Where(h => selected[h.Id] == CodingHunkDecision.Reject).ToArray();
            return new(accepted, rejected, accepted.Length > 0,
                accepted.Length == 0 ? "No accepted changes." :
                "Review complete. Applying requires a fresh preimage check, approval and transactional executor.");
        }

        private static bool IsSafeRelativePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("/", StringComparison.Ordinal)
                || value.Contains('\\') || value.Contains(':') || value.Contains('\0')) return false;
            string[] parts = value.Split('/');
            return parts.All(p => p.Length > 0 && p != "." && p != "..");
        }
    }

    public enum CodingReviewApprovalLevel { ReadOnly, Ask, AutoInSandbox }

    /// <summary>Maps the new review UI wording onto the existing permission modes without weakening its guards.</summary>
    public static class CodingReviewApprovalPresenter
    {
        public static (bool Allowed, string ExistingMode, string Reason) Select(
            CodingReviewApprovalLevel level, bool sandboxVerified)
        {
            return level switch
            {
                CodingReviewApprovalLevel.ReadOnly => (true, CodingAgentPermissionMode.Plan,
                    "Read-only planning; mutations are blocked."),
                CodingReviewApprovalLevel.Ask => (true, CodingAgentPermissionMode.Ask,
                    "Ask for exact one-shot approval before changes."),
                CodingReviewApprovalLevel.AutoInSandbox when sandboxVerified =>
                    (true, CodingAgentPermissionMode.Bypass, "Automatic actions restricted to an established OS sandbox."),
                CodingReviewApprovalLevel.AutoInSandbox =>
                    (false, CodingAgentPermissionMode.Plan, "Cannot enable auto without an established OS sandbox."),
                _ => (false, CodingAgentPermissionMode.Plan, "Unknown approval level.")
            };
        }
    }

    public sealed record CodingRepairCycle(int AttemptsUsed, int MaximumAttempts, bool VerificationPassed,
        string State)
    {
        public static CodingRepairCycle Start(int maximumAttempts = 2) =>
            new(0, Math.Clamp(maximumAttempts, 0, 5), false, "Awaiting verification");

        /// <summary>Consumes only observed results; never infers that a build or test ran.</summary>
        public CodingRepairCycle ObserveVerification(int exitCode)
        {
            if (VerificationPassed) return this;
            if (exitCode == 0) return this with { VerificationPassed = true, State = "Verified by observed command" };
            return AttemptsUsed >= MaximumAttempts
                ? this with { State = "Stopped: repair limit reached" }
                : this with { State = "Verification failed; repair available" };
        }

        public CodingRepairCycle BeginRepair()
        {
            if (VerificationPassed || AttemptsUsed >= MaximumAttempts
                || State != "Verification failed; repair available") return this;
            return this with { AttemptsUsed = AttemptsUsed + 1, State = "Repair in progress; must re-verify" };
        }

        public string Display => State + " (" + AttemptsUsed + "/" + MaximumAttempts + " repairs)";
    }
}
