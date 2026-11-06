using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SettingsSectionVisibilityPolicyTests
{
    [Theory]
    [InlineData("General", "NOTIFICATIONS")]
    [InlineData("General", "QUICK CHAT BUBBLE")]
    [InlineData("General", "FINDING YOUR WAY AROUND")]
    [InlineData("Chat Behavior", "SKILLS")]
    [InlineData("Models", "CLOUD FALLBACK (OPTIONAL)")]
    [InlineData("Privacy & Network", "BROWSER ACCESS")]
    public void Corporate_hides_nonessential_shared_sections(string category, string heading)
    {
        Assert.False(SettingsSectionVisibilityPolicy.IsVisible(
            category, heading, SetupProfile.AiOnly, corporate: true));
    }

    [Theory]
    [InlineData("General", "GENERAL")]
    [InlineData("Chat Behavior", "RAG (LOCAL DOCUMENTS)")]
    [InlineData("Chat Behavior", "PERSONAS")]
    [InlineData("Chat Behavior", "MEMORY")]
    [InlineData("Data", "IMPORT CHAT HISTORY")]
    public void Corporate_keeps_local_chat_document_and_backup_controls(string category, string heading)
    {
        Assert.True(SettingsSectionVisibilityPolicy.IsVisible(
            category, heading, SetupProfile.AiOnly, corporate: true));
    }

    [Theory]
    [InlineData("Data", "IMPORT CHAT HISTORY")]
    [InlineData("General", "QUICK CHAT BUBBLE")]
    [InlineData("Privacy & Network", "API KEYS")]
    [InlineData("Privacy & Network", "BROWSER ACCESS")]
    public void Gaming_only_hides_ai_features_inside_shared_categories(string category, string heading)
    {
        Assert.False(SettingsSectionVisibilityPolicy.IsVisible(
            category, heading, SetupProfile.GamingOnly, corporate: false));
    }

    [Fact]
    public void Ai_only_hides_gaming_privacy_but_keeps_chat_settings()
    {
        Assert.False(SettingsSectionVisibilityPolicy.IsVisible(
            "Privacy & Network", "GAMING", SetupProfile.AiOnly, false));
        Assert.True(SettingsSectionVisibilityPolicy.IsVisible(
            "Chat Behavior", "SKILLS", SetupProfile.AiOnly, false));
        Assert.True(SettingsSectionVisibilityPolicy.IsVisible(
            "Data", "IMPORT CHAT HISTORY", SetupProfile.AiOnly, false));
    }

    [Fact]
    public void Both_keeps_shared_sections_and_corporate_always_wins()
    {
        Assert.True(SettingsSectionVisibilityPolicy.IsVisible(
            "Privacy & Network", "GAMING", SetupProfile.Both, false));
        Assert.False(SettingsSectionVisibilityPolicy.IsVisible(
            "Models", "MODELS", SetupProfile.Both, true));
    }
}
