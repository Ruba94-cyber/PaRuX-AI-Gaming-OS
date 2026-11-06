namespace PaRuX.Contracts;

/// <summary>
/// Resolve the Corporate CPU text model without silently replacing a deliberate
/// per-request choice. A missing saved Chat role may recover to the packaged
/// starter so a user can still open Chat and fix the assignment in Model Hub.
/// The caller displays the recovery note; it is never a hidden model switch.
/// </summary>
public sealed record CorporateTextModelChoice(string? Path, bool RecoveredFromSavedChoice);

public static class CorporateTextModelChoicePolicy
{
    public static CorporateTextModelChoice Resolve(
        string? explicitPath, string? savedChatPath, string? bundledStarterPath,
        Func<string, bool> isUsable)
    {
        ArgumentNullException.ThrowIfNull(isUsable);

        // A caller asked for this exact model; fail honestly if it is gone.
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return new CorporateTextModelChoice(explicitPath, false);

        if (!string.IsNullOrWhiteSpace(savedChatPath))
        {
            if (isUsable(savedChatPath))
                return new CorporateTextModelChoice(savedChatPath, false);
            if (!string.IsNullOrWhiteSpace(bundledStarterPath) && isUsable(bundledStarterPath))
                return new CorporateTextModelChoice(bundledStarterPath, true);
            return new CorporateTextModelChoice(savedChatPath, false);
        }

        return new CorporateTextModelChoice(bundledStarterPath, false);
    }
}
