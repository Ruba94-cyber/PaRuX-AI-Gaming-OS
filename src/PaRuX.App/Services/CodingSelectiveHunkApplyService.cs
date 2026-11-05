using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// Applies one already reviewed text-file hunk selection using the existing
    /// journaled ShadowApplyTransaction. There is deliberately NO automatic
    /// permission mode, Git commit/push, executable launch or fallback to
    /// whole-file apply. UI/agent callers must separately obtain one-shot user
    /// approval for the exact preimage/candidate digest shown in review.
    /// </summary>
    public sealed record CodingSelectiveHunkApplyResult(
        bool Applied, string Explanation, string? UnresolvedRecoveryPath = null);

    public static class CodingSelectiveHunkApplyService
    {
        private const int MaximumFileBytes = 4_000_000;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public static CodingSelectiveHunkApplyResult Apply(
            string targetRoot, string relativePath, string reviewedUnifiedDiff,
            IReadOnlyList<CodingReviewChoice> decisions, string approvedPreimageSha256,
            string approvedCandidateSha256, bool explicitOneShotApproval,
            CancellationToken cancellationToken = default)
        {
            CodingSelectiveHunkApplyResult Refuse(string why) => new(false, why);
            if (!explicitOneShotApproval)
                return Refuse("Exact one-shot approval is required before any file is changed.");
            if (!ValidDigest(approvedPreimageSha256) || !ValidDigest(approvedCandidateSha256)
                || decisions is null || reviewedUnifiedDiff is null || reviewedUnifiedDiff.Length > 2_000_000)
                return Refuse("Approved patch, decisions and digests are missing or malformed.");
            if (string.IsNullOrWhiteSpace(targetRoot) || string.IsNullOrWhiteSpace(relativePath))
                return Refuse("Choose an existing local workspace and an exact relative file.");

            string root;
            string target;
            try
            {
                root = Path.GetFullPath(targetRoot);
                if (!Directory.Exists(root) || HasLinkedAncestor(root)
                    || relativePath.Replace('\\', '/').Split('/').Any(part =>
                        string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase))
                    || !ShadowApplyPathPolicy.IsSafe(root, root, relativePath))
                    return Refuse("Workspace path is unsafe or linked.");
                target = Path.Combine(root, relativePath);
                FileInfo info = new(target);
                if (!info.Exists || info.LinkTarget is not null
                    || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                    || info.Length > MaximumFileBytes)
                    return Refuse("Target must be an existing, regular, non-linked text file under 4 MB.");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException
                or UnauthorizedAccessException or NotSupportedException)
            {
                return Refuse("The selected workspace file could not be verified.");
            }

            byte[] sourceBytes;
            string sourceText;
            CodingHunkPreviewResult preview;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sourceBytes = File.ReadAllBytes(target);
                if (sourceBytes.Length > MaximumFileBytes || HasUtf8Bom(sourceBytes))
                    return Refuse("Oversized or BOM-encoded source needs full-file review.");
                sourceText = StrictUtf8.GetString(sourceBytes);
                if (sourceText.IndexOf('\0') >= 0)
                    return Refuse("Binary-like files require full-file review.");
                string hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
                if (!string.Equals(hash, approvedPreimageSha256,
                    StringComparison.OrdinalIgnoreCase))
                    return Refuse("The source has changed since approval. Reopen the review.");

                IReadOnlyList<CodingReviewHunk> hunks =
                    CodingHunkReviewPlanner.Parse(reviewedUnifiedDiff);
                if (hunks.Count == 0 || hunks.Any(h => !string.Equals(
                        h.RelativePath, relativePath, StringComparison.Ordinal)))
                    return Refuse("Every reviewed hunk must target this one exact file.");
                preview = CodingSelectiveHunkPreview.Create(
                    relativePath, sourceText, hunks, decisions);
                if (!preview.Ready || preview.CandidateText is null
                    || preview.CandidateSha256 is null)
                    return Refuse(preview.Explanation);
                if (!string.Equals(preview.PreimageSha256, approvedPreimageSha256,
                    StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(preview.CandidateSha256, approvedCandidateSha256,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(preview.CandidateSha256, preview.PreimageSha256,
                        StringComparison.OrdinalIgnoreCase))
                    return Refuse("The accepted hunks differ from the exact approved preview.");
            }
            catch (OperationCanceledException) { return Refuse("Selective apply cancelled before staging."); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or
                IOException or UnauthorizedAccessException or DecoderFallbackException)
            { return Refuse("The reviewed patch or source encoding is invalid: " + ex.Message); }

            // A private source folder feeds the existing durable rollback engine;
            // never write the candidate directly into the user's workspace.
            string stageRoot = Path.Combine(Path.GetTempPath(),
                "parux-hunk-stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(stageRoot);
                string stagedPath = Path.Combine(stageRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                if (!ShadowApplyPathPolicy.IsSafe(stageRoot, root, relativePath))
                    return Refuse("Staging path became unsafe.");
                byte[] candidateBytes = StrictUtf8.GetBytes(preview.CandidateText!);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(candidateBytes)),
                    approvedCandidateSha256, StringComparison.OrdinalIgnoreCase))
                    return Refuse("Staged bytes differ from the approved result.");
                using (var output = new FileStream(stagedPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    output.Write(candidateBytes);
                    output.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();

                // Git-style blob digest binds the durable snapshot to exactly
                // the preimage bytes that were displayed and approved.
                string expectedGitBlob = GitBlobSha1(sourceBytes);
                var baseline = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [relativePath] = expectedGitBlob
                };
                using ShadowApplyTransaction tx = ShadowApplyTransaction.Begin(
                    root, stageRoot, new[]
                    {
                        new ShadowApplyTransaction.Mutation("Modified", relativePath)
                    }, baseline, cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tx.Apply(cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    tx.Complete();
                    return new(true, "Applied the exact approved hunk selection with a verified journaled transaction. No commit or push.");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException
                    or UnauthorizedAccessException or OperationCanceledException)
                {
                    bool restored = tx.TryRollback();
                    return new(false, restored
                        ? "Apply was refused and the original file was retained/restored: " + ex.Message
                        : "Apply could not be safely reconciled. The recovery journal was retained: " + ex.Message,
                        restored ? null : tx.RecoveryPath);
                }
            }
            catch (OperationCanceledException)
            { return Refuse("Selective apply was cancelled; no new operation was authorized."); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Refuse("Selective apply was refused before a successful transaction: " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); }
                catch { /* Never hide the primary failure. */ }
            }
        }

        private static bool HasUtf8Bom(byte[] bytes) =>
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        private static bool ValidDigest(string? digest) =>
            digest is { Length: 64 } && digest.All(Uri.IsHexDigit);

        private static bool HasLinkedAncestor(string directory)
        {
            string? cursor = Path.GetFullPath(directory);
            while (cursor is not null)
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    return true;
                string? parent = Path.GetDirectoryName(cursor);
                if (parent is null || string.Equals(parent, cursor,
                    StringComparison.OrdinalIgnoreCase)) break;
                cursor = parent;
            }
            return false;
        }

        private static string GitBlobSha1(byte[] bytes)
        {
            byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
            using var sha = SHA1.Create();
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformFinalBlock(bytes, 0, bytes.Length);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
    }
}
