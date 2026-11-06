using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services;

/// <summary>Shows only settings relevant to the installed profile. This controls the
/// category rail, compact picker, search results and active content together; it is
/// presentation policy, not an authorization boundary for the Core API.</summary>
public static class SettingsCategoryPolicy
{
    private static readonly HashSet<string> CorporateCategories = new(StringComparer.Ordinal)
    {
        "General", "Models", "Chat Behavior", "Privacy & Network", "Data"
    };

    private static readonly HashSet<string> GamingCategories = new(StringComparer.Ordinal)
    {
        "General", "Gaming & AI Sharing", "Privacy & Network", "Data"
    };

    public static IReadOnlyList<string> VisibleCategories(
        IEnumerable<string> categories, SetupProfile profile, bool corporate)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return categories.Where(category => corporate
            ? CorporateCategories.Contains(category)
            : profile == SetupProfile.GamingOnly
                ? GamingCategories.Contains(category)
                : profile != SetupProfile.AiOnly
                    || !string.Equals(category, "Gaming & AI Sharing", StringComparison.Ordinal))
            .ToList().AsReadOnly();
    }
}
