using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// The coding-task card must never say "Verified" for a completed task whose verification is
/// unverified, inconclusive or failed. The Debug-only UI fixture takes its VerificationStatus from
/// PARUX_UI_TEST_CODING_VERIFICATION (inherited by the launched app).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CodingVerificationHonestyTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "CodingAgent"), Trait("Category", "FlaUI")]
    public void Verified_task_has_a_named_hunk_review_entry_point()
    {
        const string name = nameof(Verified_task_has_a_named_hunk_review_entry_point);
        string? prior = Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION");
        try
        {
            Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", "pass");
            using var app = new UiTestApplication(name, enableCompletedCodingTaskCardFixture: true);
            try
            {
                var action = UiWait.ById(app.MainWindow,
                    "Chat_CodingTask_ReviewHunks_Button", ControlType.Button);
                Assert.NotNull(action);
                // The synthetic fixture has no real reviewed files. Presence
                // does not authorize or initiate a host Git/file mutation.
                Assert.False(action.Properties.IsEnabled.ValueOrDefault);
            }
            catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", prior);
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "CodingAgent"), Trait("Category", "FlaUI")]
    public void Hunk_review_control_is_not_exposed_for_unverified_task()
    {
        const string name = nameof(Hunk_review_control_is_not_exposed_for_unverified_task);
        string? prior = Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION");
        try
        {
            Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", "unverified");
            using var app = new UiTestApplication(name, enableCompletedCodingTaskCardFixture: true);
            try
            {
                UiWait.ById(app.MainWindow, "parux-coding-review-unverified", ControlType.Button);
                Assert.Null(app.MainWindow.FindFirstDescendant(
                    cf => cf.ByAutomationId("Chat_CodingTask_ReviewHunks_Button")));
            }
            catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", prior);
        }
    }

    [Theory, Trait("Category", "Regression"), Trait("Category", "CodingAgent"), Trait("Category", "FlaUI")]
    [InlineData("pass", "Completed and verified")]
    [InlineData("unverified", "Completed, not verified")]
    [InlineData("inconclusive", "Completed, verification not confirmed")]
    public void Completed_card_badge_reflects_only_what_core_verified(string verificationStatus, string expectedBadge)
    {
        string testName = nameof(Completed_card_badge_reflects_only_what_core_verified) + "-" + verificationStatus;
        string? previous = Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION");
        Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", verificationStatus);
        try
        {
            using var app = new UiTestApplication(testName, enableCompletedCodingTaskCardFixture: true);
            try
            {
                var badge = UiWait.ById(app.MainWindow, "Chat_CodingTask_Status_Text", ControlType.Text);
                UiWait.For(() => badge.Properties.Name.ValueOrDefault == expectedBadge, "badge text '" + expectedBadge + "'");
                if (verificationStatus != "pass")
                    Assert.NotEqual("Completed and verified", badge.Properties.Name.ValueOrDefault);
                app.CaptureSnapshot("badge-" + verificationStatus);
            }
            catch (Exception ex)
            {
                app.CaptureFailure(testName, ex);
                throw;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", previous);
        }
    }
}
