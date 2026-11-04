using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SetupProfilePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("both")]
    public void Existing_installations_and_unknown_values_mean_both(string? stored) =>
        Assert.Equal(SetupProfile.Both, SetupProfilePolicy.Parse(stored));

    [Theory]
    [InlineData("ai", SetupProfile.AiOnly)]
    [InlineData("AI", SetupProfile.AiOnly)]
    [InlineData(" gaming ", SetupProfile.GamingOnly)]
    public void Stored_values_parse_case_and_whitespace_insensitively(string stored, SetupProfile expected) =>
        Assert.Equal(expected, SetupProfilePolicy.Parse(stored));

    [Fact]
    public void Storage_round_trips_every_profile()
    {
        foreach (SetupProfile profile in Enum.GetValues<SetupProfile>())
            Assert.Equal(profile, SetupProfilePolicy.Parse(SetupProfilePolicy.ToStorage(profile)));
    }

    [Fact]
    public void Both_starts_everything_and_shows_both_tabs()
    {
        SetupProfilePlan plan = SetupProfilePolicy.Plan(SetupProfile.Both);

        Assert.True(plan.StartLocalAi && plan.StartGaming && plan.StartAgentWorkers);
        Assert.True(plan.RequireLanguageModel && plan.ShowAiWorkspaces && plan.ShowGamingWorkspace && plan.ShowModeTabs);
        Assert.False(plan.StartInGamingTab);
    }

    [Fact]
    public void Ai_only_does_not_start_or_show_gaming()
    {
        SetupProfilePlan plan = SetupProfilePolicy.Plan(SetupProfile.AiOnly);

        Assert.False(plan.StartGaming);
        Assert.False(plan.ShowGamingWorkspace);
        Assert.False(plan.ShowModeTabs);
        Assert.True(plan.StartLocalAi && plan.RequireLanguageModel && plan.ShowAiWorkspaces);
    }

    [Fact]
    public void Gaming_only_needs_no_language_model_and_starts_no_ai_services()
    {
        SetupProfilePlan plan = SetupProfilePolicy.Plan(SetupProfile.GamingOnly);

        Assert.False(plan.RequireLanguageModel);
        Assert.False(plan.StartLocalAi);
        Assert.False(plan.StartAgentWorkers);
        Assert.False(plan.ShowAiWorkspaces);
        Assert.False(plan.ShowModeTabs);
        Assert.True(plan.StartGaming && plan.ShowGamingWorkspace && plan.StartInGamingTab);
    }

    [Fact]
    public void Every_profile_has_a_label_and_description_in_both_languages_and_a_reassurance_note()
    {
        foreach (SetupProfile profile in Enum.GetValues<SetupProfile>())
        foreach (bool italian in new[] { false, true })
        {
            Assert.False(string.IsNullOrWhiteSpace(SetupProfilePolicy.Label(profile, italian)));
            Assert.False(string.IsNullOrWhiteSpace(SetupProfilePolicy.Description(profile, italian)));
        }
        Assert.Equal("Solo IA", SetupProfilePolicy.Label(SetupProfile.AiOnly, true));
        Assert.Equal("Entrambi", SetupProfilePolicy.Label(SetupProfile.Both, true));
        Assert.Contains("deletes nothing", SetupProfilePolicy.SwitchNote(false));
        Assert.Contains("non cancella nulla", SetupProfilePolicy.SwitchNote(true));
    }

    [Fact]
    public void Descriptions_use_no_backend_or_quantisation_jargon()
    {
        foreach (SetupProfile profile in Enum.GetValues<SetupProfile>())
        foreach (bool italian in new[] { false, true })
        {
            string text = SetupProfilePolicy.Description(profile, italian);
            foreach (string term in new[] { "GGUF", "llama", "quant", "VRAM", "CUDA", "backend" })
                Assert.DoesNotContain(term, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
