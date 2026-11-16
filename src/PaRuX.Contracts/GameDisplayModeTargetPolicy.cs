namespace PaRuX.Contracts;

/// <summary>
/// Pure monitor selection for the explicit Gaming Devices mode-change panel.
/// A chosen TV must still exist and be assigned as TV; never silently fall
/// back to the primary screen when a target is disconnected or reidentified.
/// </summary>
public static class GameDisplayModeTargetPolicy
{
    public static GamingDisplayModeDto? Select(GamingDisplaySnapshotDto? snapshot,
        IEnumerable<ConsoleDisplayAssignment>? roles, string? configuredTvId)
    {
        if (snapshot is not { Supported: true }) return null;
        if (!string.IsNullOrWhiteSpace(configuredTvId))
            return ConsoleDisplaySelectionPolicy.SelectTarget(snapshot, roles, configuredTvId);

        GamingDisplayModeDto[] primary = snapshot.Displays.Where(d => d.Primary)
            .Take(2).ToArray();
        return primary.Length == 1 ? primary[0] : null;
    }

    /// <summary>
    /// Changing resolution or refresh can cause Windows/driver to leave HDR
    /// or WCG. Until a single transactional color+mode restore exists, protect
    /// advanced-color displays by refusing raw ChangeDisplaySettingsEx.
    /// Unknown mode is disclosed by UI, not treated as verified SDR/HDR.
    /// </summary>
    public static bool NeedsColorSafeTransaction(GamingDisplayModeDto display) =>
        display.ColorMode is "hdr" or "wcg" or "advanced-color-unknown" ||
        display.HdrEnabled == true;
}
