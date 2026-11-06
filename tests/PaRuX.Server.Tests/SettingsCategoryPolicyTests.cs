using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SettingsCategoryPolicyTests
{
    private static readonly string[] All =
    {
        "General", "Models", "Chat Behavior", "Your PC apps", "Media Generation",
        "Automation", "Agents", "Integrations", "Gaming & AI Sharing",
        "PaRuX Platform", "Privacy & Network", "Data", "Advanced"
    };

    [Fact]
    public void Corporate_settings_keep_only_conversation_models_privacy_and_data()
    {
        string[] expected = { "General", "Models", "Chat Behavior", "Privacy & Network", "Data" };
        foreach (SetupProfile p in Enum.GetValues<SetupProfile>())
            Assert.Equal(expected, SettingsCategoryPolicy.VisibleCategories(All, p, corporate: true));
    }

    [Fact]
    public void Gaming_only_cannot_search_or_open_model_automation_and_media_settings()
    {
        Assert.Equal(new[] { "General", "Gaming & AI Sharing", "Privacy & Network", "Data" },
            SettingsCategoryPolicy.VisibleCategories(All, SetupProfile.GamingOnly, corporate: false));
    }

    [Fact]
    public void Ai_only_removes_gaming_and_both_preserves_existing_categories()
    {
        IReadOnlyList<string> ai = SettingsCategoryPolicy.VisibleCategories(All, SetupProfile.AiOnly, false);
        Assert.DoesNotContain("Gaming & AI Sharing", ai);
        Assert.Contains("Models", ai);
        Assert.Contains("Chat Behavior", ai);
        Assert.Equal(All, SettingsCategoryPolicy.VisibleCategories(All, SetupProfile.Both, false));
    }
}
