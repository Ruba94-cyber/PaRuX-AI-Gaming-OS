using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingHunkReviewPlannerTests
{
    private const string Diff =
        "diff --git a/src/demo.cs b/src/demo.cs\n" +
        "--- a/src/demo.cs\n+++ b/src/demo.cs\n" +
        "@@ -1,2 +1,2 @@\n one\n-old\n+new\n" +
        "@@ -5 +5 @@\n-before\n+after\n";

    [Fact]
    public void Parses_two_hunks_and_separately_accepts_only_one()
    {
        var hunks = CodingHunkReviewPlanner.Parse(Diff);
        Assert.Equal(2, hunks.Count);
        Assert.Equal("src/demo.cs", hunks[0].RelativePath);
        Assert.Equal(2, hunks[0].OldCount);
        var result = CodingHunkReviewPlanner.Plan(hunks, new[]
        {
            new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Reject),
            new CodingReviewChoice(hunks[1].Id, CodingHunkDecision.Accept)
        });
        Assert.True(result.Ready);
        Assert.Single(result.Accepted);
        Assert.Single(result.Rejected);
        Assert.Equal(hunks[1].Id, result.Accepted[0].Id);
    }

    [Fact]
    public void Rejects_unscoped_or_malformed_hunks()
    {
        Assert.Throws<FormatException>(() => CodingHunkReviewPlanner.Parse(
            "@@ -1 +1 @@\n-old\n+new"));
        Assert.Throws<FormatException>(() => CodingHunkReviewPlanner.Parse(
            "diff --git a/x b/x\n+++ b/../outside\n@@ -1 +1 @@\n-old\n+new"));
        Assert.Throws<FormatException>(() => CodingHunkReviewPlanner.Parse(
            "diff --git a/x b/x\n+++ b/x\n@@ -1,2 +1 @@\n-old\n+new"));
    }

    [Fact]
    public void All_hunks_must_be_reviewed_once_and_pending_cannot_apply()
    {
        var hunks = CodingHunkReviewPlanner.Parse(Diff);
        Assert.False(CodingHunkReviewPlanner.Plan(hunks, Array.Empty<CodingReviewChoice>()).Ready);
        Assert.False(CodingHunkReviewPlanner.Plan(hunks, new[]
        {
            new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept),
            new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept)
        }).Ready);
        Assert.False(CodingHunkReviewPlanner.Plan(hunks, new[]
        {
            new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Pending),
            new CodingReviewChoice(hunks[1].Id, CodingHunkDecision.Reject)
        }).Ready);
    }

    [Fact]
    public void Auto_requires_verified_sandbox_and_read_only_maps_to_existing_plan()
    {
        var read = CodingReviewApprovalPresenter.Select(CodingReviewApprovalLevel.ReadOnly, false);
        Assert.True(read.Allowed);
        Assert.Equal(CodingAgentPermissionMode.Plan, read.ExistingMode);
        Assert.False(CodingReviewApprovalPresenter.Select(
            CodingReviewApprovalLevel.AutoInSandbox, false).Allowed);
        var automatic = CodingReviewApprovalPresenter.Select(
            CodingReviewApprovalLevel.AutoInSandbox, true);
        Assert.True(automatic.Allowed);
        Assert.Equal(CodingAgentPermissionMode.Bypass, automatic.ExistingMode);
    }

    [Fact]
    public void Repair_requires_observed_failure_and_stops_at_bounded_limit()
    {
        var state = CodingRepairCycle.Start(1);
        Assert.Equal(0, state.BeginRepair().AttemptsUsed);
        state = state.ObserveVerification(1).BeginRepair();
        Assert.Equal(1, state.AttemptsUsed);
        Assert.False(state.VerificationPassed);
        state = state.ObserveVerification(1);
        Assert.Contains("limit", state.State, StringComparison.Ordinal);
        Assert.Equal(state, state.BeginRepair());
        Assert.True(CodingRepairCycle.Start().ObserveVerification(0).VerificationPassed);
    }
    [Fact]
    public void Deleted_and_added_lines_with_header_looking_prefixes_are_not_misparsed()
    {
        const string diff = "diff --git a/demo.txt b/demo.txt\n" +
            "--- a/demo.txt\n+++ b/demo.txt\n" +
            "@@ -1 +1 @@\n---- original\n++++ replacement\n";
        var hunks = CodingHunkReviewPlanner.Parse(diff);
        Assert.Single(hunks);
        Assert.Equal("---- original\n++++ replacement", hunks[0].Text);
        var preview = CodingSelectiveHunkPreview.Create("demo.txt", "--- original\n", hunks,
            new[] { new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept) });
        Assert.True(preview.Ready, preview.Explanation);
        Assert.Equal("+++ replacement\n", preview.CandidateText);
    }

    [Fact]
    public void Selective_preview_checks_all_hunks_but_changes_only_accepted_ones()
    {
        var hunks = CodingHunkReviewPlanner.Parse(Diff);
        const string before = "one\nold\nmiddle\nx\nbefore\nlast\n";
        var preview = CodingSelectiveHunkPreview.Create("src/demo.cs", before, hunks,
            new[]
            {
                new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept),
                new CodingReviewChoice(hunks[1].Id, CodingHunkDecision.Reject)
            });
        Assert.True(preview.Ready, preview.Explanation);
        Assert.Equal("one\nnew\nmiddle\nx\nbefore\nlast\n", preview.CandidateText);
        Assert.True(CodingSelectiveHunkPreview.PreimageMatches(preview.PreimageSha256, before));
        Assert.False(CodingSelectiveHunkPreview.PreimageMatches(
            preview.PreimageSha256, "one\nchanged\nmiddle\nx\nbefore\nlast\n"));
        Assert.NotEqual(preview.PreimageSha256, preview.CandidateSha256);
    }

    [Fact]
    public void Preview_preserves_crlf_without_changing_the_original()
    {
        const string diff = "diff --git a/test.cs b/test.cs\n" +
            "--- a/test.cs\n+++ b/test.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var h = Assert.Single(CodingHunkReviewPlanner.Parse(diff));
        const string original = "old\r\n";
        var preview = CodingSelectiveHunkPreview.Create("test.cs", original, new[] { h },
            new[] { new CodingReviewChoice(h.Id, CodingHunkDecision.Accept) });
        Assert.True(preview.Ready, preview.Explanation);
        Assert.Equal("new\r\n", preview.CandidateText);
        Assert.Equal("old\r\n", original);
    }

    [Fact]
    public void Preview_fails_closed_for_stale_source_unreviewed_hunks_and_no_newline_markers()
    {
        var hunks = CodingHunkReviewPlanner.Parse(Diff);
        Assert.False(CodingSelectiveHunkPreview.Create("src/demo.cs",
            "one\nchanged\nmiddle\nx\nbefore\nlast\n", hunks,
            new[]
            {
                new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept),
                new CodingReviewChoice(hunks[1].Id, CodingHunkDecision.Reject)
            }).Ready);
        Assert.False(CodingSelectiveHunkPreview.Create("src/demo.cs", "one\nold\n", hunks,
            new[] { new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept) }).Ready);

        const string diff = "diff --git a/test.cs b/test.cs\n" +
            "--- a/test.cs\n+++ b/test.cs\n@@ -1 +1 @@\n-old\n+new\n" +
            "\\ No newline at end of file\n";
        var h = Assert.Single(CodingHunkReviewPlanner.Parse(diff));
        Assert.False(CodingSelectiveHunkPreview.Create("test.cs", "old", new[] { h },
            new[] { new CodingReviewChoice(h.Id, CodingHunkDecision.Accept) }).Ready);
    }

    [Fact]
    public void Empty_preimage_can_still_be_compared_but_paths_must_be_scoped()
    {
        string emptyHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("")));
        Assert.True(CodingSelectiveHunkPreview.PreimageMatches(emptyHash, ""));
        var hunks = CodingHunkReviewPlanner.Parse(Diff);
        Assert.False(CodingSelectiveHunkPreview.Create("../untrusted.cs", "one\nold\n", hunks,
            new[]
            {
                new CodingReviewChoice(hunks[0].Id, CodingHunkDecision.Accept),
                new CodingReviewChoice(hunks[1].Id, CodingHunkDecision.Reject)
            }).Ready);
    }
}
