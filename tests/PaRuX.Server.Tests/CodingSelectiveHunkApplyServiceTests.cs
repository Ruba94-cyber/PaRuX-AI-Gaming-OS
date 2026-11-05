using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real local-file transaction tests on disposable synthetic folders.
/// These do not invoke a real coding model, Git push or the Windows UI.
/// </summary>
public sealed class CodingSelectiveHunkApplyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-hunk-apply-test-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_root, "code.txt");

    private const string Diff =
        "diff --git a/code.txt b/code.txt\n" +
        "--- a/code.txt\n+++ b/code.txt\n" +
        "@@ -1,2 +1,2 @@\n one\n-old\n+new\n" +
        "@@ -4 +4 @@\n-last\n+changed\n";

    public CodingSelectiveHunkApplyServiceTests() =>
        Directory.CreateDirectory(_root);

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private (CodingReviewChoice[] Decisions, CodingHunkPreviewResult Preview)
        Approve(string before, bool takeSecond = false)
    {
        var hunks = CodingHunkReviewPlanner.Parse(Diff);
        var decisions = new[]
        {
            new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept),
            new CodingReviewChoice(hunks[1].Id,
                takeSecond ? CodingHunkDecision.Accept : CodingHunkDecision.Reject)
        };
        var preview = CodingSelectiveHunkPreview.Create("code.txt", before, hunks, decisions);
        Assert.True(preview.Ready, preview.Explanation);
        return (decisions, preview);
    }

    [Fact]
    public void Exact_one_shot_approval_applies_only_selected_hunk_through_journal()
    {
        const string before = "one\nold\nmiddle\nlast\n";
        File.WriteAllText(Target, before);
        var approved = Approve(before);
        var result = CodingSelectiveHunkApplyService.Apply(
            _root, "code.txt", Diff, approved.Decisions,
            approved.Preview.PreimageSha256, approved.Preview.CandidateSha256!,
            explicitOneShotApproval: true);

        Assert.True(result.Applied, result.Explanation);
        Assert.Equal("one\nnew\nmiddle\nlast\n", File.ReadAllText(Target));
        Assert.Null(result.UnresolvedRecoveryPath);
        Assert.False(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Missing_approval_or_wrong_candidate_digest_never_changes_user_file()
    {
        const string before = "one\nold\nmiddle\nlast\n";
        File.WriteAllText(Target, before);
        var approved = Approve(before);
        var refused = CodingSelectiveHunkApplyService.Apply(
            _root, "code.txt", Diff, approved.Decisions,
            approved.Preview.PreimageSha256, approved.Preview.CandidateSha256!,
            explicitOneShotApproval: false);
        Assert.False(refused.Applied);

        refused = CodingSelectiveHunkApplyService.Apply(
            _root, "code.txt", Diff, approved.Decisions,
            approved.Preview.PreimageSha256, Sha("unexpected"),
            explicitOneShotApproval: true);
        Assert.False(refused.Applied);
        Assert.Equal(before, File.ReadAllText(Target));
    }

    [Fact]
    public void Human_edit_since_review_is_preserved_instead_of_overwritten()
    {
        const string before = "one\nold\nmiddle\nlast\n";
        File.WriteAllText(Target, before);
        var approved = Approve(before);
        File.WriteAllText(Target, "outside modification\n");
        var refused = CodingSelectiveHunkApplyService.Apply(
            _root, "code.txt", Diff, approved.Decisions,
            approved.Preview.PreimageSha256, approved.Preview.CandidateSha256!,
            explicitOneShotApproval: true);
        Assert.False(refused.Applied);
        Assert.Equal("outside modification\n", File.ReadAllText(Target));
    }

    [Fact]
    public void No_patch_may_target_git_metadata_or_a_different_reviewed_path()
    {
        const string before = "one\nold\nmiddle\nlast\n";
        File.WriteAllText(Target, before);
        var approved = Approve(before);
        var wrong = CodingSelectiveHunkApplyService.Apply(
            _root, ".git/config", Diff, approved.Decisions,
            approved.Preview.PreimageSha256, approved.Preview.CandidateSha256!,
            explicitOneShotApproval: true);
        Assert.False(wrong.Applied);
        wrong = CodingSelectiveHunkApplyService.Apply(
            _root, "another.txt", Diff, approved.Decisions,
            approved.Preview.PreimageSha256, approved.Preview.CandidateSha256!,
            explicitOneShotApproval: true);
        Assert.False(wrong.Applied);
        Assert.Equal(before, File.ReadAllText(Target));
    }

    [Fact]
    public void Concurrent_file_edit_at_final_replace_gate_keeps_the_human_edit_and_journal()
    {
        const string before = "one\nold\nmiddle\nlast\n";
        File.WriteAllText(Target, before);
        var approved = Approve(before);
        using (ShadowApplyFaults.Install((point, _) =>
        {
            if (point == "shadow.before-replace")
                File.WriteAllText(Target, "human edit during replace\n");
        }))
        {
            var result = CodingSelectiveHunkApplyService.Apply(
                _root, "code.txt", Diff, approved.Decisions,
                approved.Preview.PreimageSha256, approved.Preview.CandidateSha256!,
                explicitOneShotApproval: true);
            Assert.False(result.Applied);
            Assert.Equal("human edit during replace\n", File.ReadAllText(Target));
            Assert.NotNull(result.UnresolvedRecoveryPath);
            Assert.True(File.Exists(Path.Combine(result.UnresolvedRecoveryPath!, "PENDING.json")));
            // Test-only cleanup of a synthetic unresolved journal. Production
            // requires owner recovery review and never deletes it automatically.
            Directory.Delete(result.UnresolvedRecoveryPath!, recursive: true);
        }
    }

    [Fact]
    public void Invalid_utf8_with_even_a_matching_hash_is_not_modified()
    {
        byte[] bytes = { 0xFF, 0xFE, 0x00, 0x80 };
        File.WriteAllBytes(Target, bytes);
        var approved = Approve("one\nold\nmiddle\nlast\n");
        var result = CodingSelectiveHunkApplyService.Apply(
            _root, "code.txt", Diff, approved.Decisions,
            Convert.ToHexString(SHA256.HashData(bytes)),
            approved.Preview.CandidateSha256!, true);
        Assert.False(result.Applied);
        Assert.Equal(bytes, File.ReadAllBytes(Target));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
