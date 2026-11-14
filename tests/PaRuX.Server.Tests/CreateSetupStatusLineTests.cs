using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The Create status line: the periodic setup refresh may update setup text but must never erase a validation error or result.</summary>
public sealed class CreateSetupStatusLineTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(CreateSetupStatusLine.Checking)]
    [InlineData(CreateSetupStatusLine.Ready)]
    [InlineData(CreateSetupStatusLine.NeedsSetup)]
    public void Setup_messages_and_empty_text_may_be_replaced_by_the_refresh(string? current) =>
        Assert.True(CreateSetupStatusLine.MayReplace(current));

    [Theory]
    [InlineData("Enter text to synthesize.")]
    [InlineData("Choose a source image first.")]
    [InlineData("Generation failed: An image prompt is required.")]
    [InlineData("No Create job is active.")]
    [InlineData("Generation completed locally and registered in Artifact Workspace.")]
    [InlineData("Generating image… · 40%")]
    public void Validation_errors_results_and_progress_are_never_overwritten(string current) =>
        Assert.False(CreateSetupStatusLine.MayReplace(current));

    [Theory]
    [InlineData("Image", null, CreateSetupStatusLine.Checking)]
    [InlineData("Image", false, CreateSetupStatusLine.NeedsSetup)]
    [InlineData("Image", true, CreateSetupStatusLine.Ready)]
    [InlineData("Gallery", false, "")]
    [InlineData("Gallery", null, "")]
    [InlineData("Video", false, "")]
    [InlineData("Speech", true, "")]
    [InlineData(null, false, "")]
    public void The_image_setup_banner_is_only_shown_on_the_image_tab(string? mode, bool? ready, string expected) =>
        Assert.Equal(expected, CreateSetupStatusLine.ForMode(mode, ready));

    [Fact]
    public void The_italian_setup_lines_are_distinct_replaceable_and_never_shown_on_other_tabs()
    {
        Assert.Equal(CreateSetupStatusLine.ItNeedsSetup, CreateSetupStatusLine.ForMode("Image", false, italian: true));
        Assert.Equal(CreateSetupStatusLine.ItReady, CreateSetupStatusLine.ForMode("Image", true, italian: true));
        Assert.Equal(CreateSetupStatusLine.ItChecking, CreateSetupStatusLine.ForMode("Image", null, italian: true));
        Assert.Equal(string.Empty, CreateSetupStatusLine.ForMode("Gallery", false, italian: true));
        Assert.NotEqual(CreateSetupStatusLine.NeedsSetup, CreateSetupStatusLine.ItNeedsSetup);
        Assert.True(CreateSetupStatusLine.MayReplace(CreateSetupStatusLine.ItNeedsSetup));
        Assert.True(CreateSetupStatusLine.MayReplace(CreateSetupStatusLine.ItReady));
        Assert.True(CreateSetupStatusLine.MayReplace(CreateSetupStatusLine.ItChecking));
    }

    [Fact]
    public void An_empty_line_on_other_tabs_may_be_refilled_when_the_image_tab_returns() =>
        Assert.True(CreateSetupStatusLine.MayReplace(CreateSetupStatusLine.ForMode("Gallery", false)));

    [Fact]
    public void The_setup_state_maps_to_distinct_honest_messages()
    {
        Assert.Equal(CreateSetupStatusLine.Ready, CreateSetupStatusLine.For(true));
        Assert.Equal(CreateSetupStatusLine.NeedsSetup, CreateSetupStatusLine.For(false));
        Assert.StartsWith("Image generation needs a one-time setup", CreateSetupStatusLine.NeedsSetup);
    }
}
