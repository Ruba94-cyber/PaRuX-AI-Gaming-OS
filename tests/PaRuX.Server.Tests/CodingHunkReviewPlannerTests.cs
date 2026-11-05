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
}
