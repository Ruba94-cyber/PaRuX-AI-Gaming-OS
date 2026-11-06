using System;

namespace PaRuX.Services;

/// <summary>
/// Pure visibility rule for cards that share a settings category. The outer
/// category filter is not sufficient: General, Data and Chat Behavior contain
/// controls for different setup profiles. Section titles here are the stable
/// XAML card keys; this never grants a Core capability or changes stored data.
/// </summary>
public static class SettingsSectionVisibilityPolicy
{
    public static bool IsVisible(
        string? category, string? sectionHeading, SetupProfile selectedProfile, bool corporate)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        string heading = (sectionHeading ?? "").Trim();
        if (corporate)
            return category switch
            {
                "General" => heading is not ("NOTIFICATIONS" or "FINDING YOUR WAY AROUND" or "QUICK CHAT BUBBLE"),
                "Chat Behavior" => heading != "SKILLS",
                "Data" => true, // backup and local history remain valuable in Corporate
                "Privacy & Network" => false, // replaced by a safe local-only summary
                "Models" => false, // replaced by bundled CPU model manager
                _ => true
            };

        if (selectedProfile == SetupProfile.GamingOnly)
            return category switch
            {
                "General" => heading is not ("NOTIFICATIONS" or "QUICK CHAT BUBBLE" or "FINDING YOUR WAY AROUND"),
                "Data" => heading != "IMPORT CHAT HISTORY",
                "Privacy & Network" => heading is not ("BROWSER ACCESS" or "API KEYS" or "INTERNET"),
                _ => true
            };

        if (selectedProfile == SetupProfile.AiOnly)
            return !(category == "Privacy & Network" && heading == "GAMING");

        return true;
    }
}
