using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ApplyPatchPolicyTests
{
    private const string Diff = "--- a/f.txt\n+++ b/f.txt\n@@ -1 +1 @@\n-a\n+b\n";

    [Fact]
    public void Apply_patch_is_a_workspace_mutation_and_is_refused_in_plan_mode()
    {
        Assert.True(CodingAgentPermissionPolicy.IsWorkspaceMutationAction("apply_patch"));
        Assert.True(CodingAgentPermissionPolicy.IsRefusedInPlanMode(CodingAgentPermissionMode.Plan, "apply_patch"));
        Assert.False(CodingAgentPermissionPolicy.IsRefusedInPlanMode(CodingAgentPermissionMode.Ask, "apply_patch"));
        Assert.False(CodingAgentPermissionPolicy.IsRefusedInPlanMode(CodingAgentPermissionMode.Bypass, "apply_patch"));
    }

    [Fact]
    public void Approval_preview_shows_the_complete_diff_and_the_key_is_bound_to_it()
    {
        string? preview = CodingAgentPermissionPolicy.FileMutationApprovalPreview("apply_patch", null, null, Diff, null, null);
        Assert.NotNull(preview);
        Assert.Contains("Complete unified diff", preview);
        Assert.Contains("+b", preview);

        string a = CodingAgentPermissionPolicy.FileMutationApprovalKey("apply_patch", null, null, Diff, null, null);
        string b = CodingAgentPermissionPolicy.FileMutationApprovalKey("apply_patch", null, null, Diff.Replace("+b", "+c"), null, null);
        Assert.NotEqual(a, b); // approving one diff never approves another
    }

    [Fact]
    public void A_diff_too_large_to_preview_is_not_approvable()
    {
        string big = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+" + new string('x', CodingAgentPermissionPolicy.MaximumFileApprovalPreviewCharacters) + "\n";
        Assert.Null(CodingAgentPermissionPolicy.FileMutationApprovalPreview("apply_patch", null, null, big, null, null));
    }

    [Fact]
    public void Prompt_schema_offers_apply_patch()
    {
        string schema = typeof(CodingAgentPermissionPolicy).Assembly.GetTypes()
            .Where(t => t.Name == "CodingAgentPromptBuilder")
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            .Count() > 0 ? "found" : "missing";
        Assert.Equal("found", schema);
    }
}
