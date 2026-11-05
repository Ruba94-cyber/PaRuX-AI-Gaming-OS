using System;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Final, side-effect-free admission for a user-approved subset of a fully
    /// verified task. It requires stable Core verification, repository authority,
    /// original destination, entire worktree fingerprint and selected Git diff.
    /// This is only a precondition: the transactional file service rechecks the
    /// exact preimage and candidate hashes under the project lease.
    /// </summary>
    public static class CodingSelectiveHunkAdmissionPolicy
    {
        public static bool Allows(
            bool coreApproved, string? verifiedAtReview, string? verifiedNow,
            string? fullTaskFingerprintAtReview, string? fullTaskFingerprintNow,
            string? repositoryAtReview, string? repositoryNow,
            string? targetAtReview, string? targetNow,
            string? diffAtReview, string? diffNow)
        {
            if (!coreApproved || !ValidDigest(verifiedAtReview)
                || !string.Equals(verifiedAtReview, verifiedNow, StringComparison.Ordinal)
                || !string.Equals(verifiedAtReview, fullTaskFingerprintAtReview, StringComparison.Ordinal)
                || !string.Equals(verifiedAtReview, fullTaskFingerprintNow, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(diffAtReview) || diffAtReview.Length > 2_000_000
                || !string.Equals(diffAtReview, diffNow, StringComparison.Ordinal))
                return false;
            return SameFullPath(repositoryAtReview, repositoryNow)
                && SameFullPath(targetAtReview, targetNow);
        }

        private static bool ValidDigest(string? value) =>
            value is { Length: 64 } &&
            value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static bool SameFullPath(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return false;
            try
            {
                if (!Path.IsPathFullyQualified(first) || !Path.IsPathFullyQualified(second))
                    return false;
                string left = Path.GetFullPath(first).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string right = Path.GetFullPath(second).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(left, right, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                or PathTooLongException)
            {
                return false;
            }
        }
    }
}
