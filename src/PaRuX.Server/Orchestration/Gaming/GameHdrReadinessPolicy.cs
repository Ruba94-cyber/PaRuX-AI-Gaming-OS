using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Per-game, purely observational HDR gate. It cannot change system HDR, WCG,
/// monitor modes, refresh or VRR. Default is no request; "prefer" is advisory;
/// "require" refuses before process start unless the exact selected display
/// explicitly reports HDR active (not just WCG or older Advanced Color).
/// </summary>
internal static class GameHdrReadinessPolicy
{
    internal const string System = "system", Prefer = "prefer", Require = "require";
    internal const string NotRequested = "", Ready = "hdr-ready",
        PreferredUnavailable = "hdr-preference-not-ready";

    internal sealed record Verdict(bool MayLaunch, string Status, string? Refusal);

    internal static Verdict Check(string? mode, string? displayId, GamingDisplaySnapshotDto? topology)
    {
        if (mode is null or System) return new(true, NotRequested, null);
        if (mode is not (Prefer or Require))
            return new(false, "invalid", "Invalid saved HDR launch preference.");

        // A required HDR target must have been chosen explicitly. On a dual
        // monitor setup, the primary display is not necessarily the game TV.
        if (mode == Require && !ConsoleDisplaySelectionPolicy.ValidDisplayId(displayId))
            return new(false, "display-not-selected",
                "Choose an exact connected display before requiring HDR for this game.");

        GamingDisplayModeDto? selected = null;
        if (topology?.Supported == true)
        {
            if (ConsoleDisplaySelectionPolicy.ValidDisplayId(displayId))
            {
                GamingDisplayModeDto[] matches = topology.Displays.Where(d =>
                    string.Equals(d.DisplayId, displayId, StringComparison.OrdinalIgnoreCase))
                    .Take(2).ToArray();
                if (matches.Length == 1) selected = matches[0];
            }
            else
            {
                GamingDisplayModeDto[] primary = topology.Displays.Where(d => d.Primary)
                    .Take(2).ToArray();
                if (primary.Length == 1) selected = primary[0];
            }
        }

        bool ready = selected is { HdrSupported: true, HdrEnabled: true, ColorMode: "hdr" };
        if (ready) return new(true, Ready, null);
        if (mode == Prefer) return new(true, PreferredUnavailable, null);

        string reason = selected switch
        {
            null => "The selected HDR display is disconnected or its identity could not be verified.",
            { HdrSupported: false } => "Windows reports that the selected display does not support HDR.",
            { ColorMode: "wcg" } => "The selected display is using WCG, not HDR.",
            { HdrSupported: true, HdrEnabled: false } =>
                "HDR is supported but not enabled in Windows on the selected display.",
            _ => "Windows did not confirm that HDR is enabled on the selected display."
        };
        return new(false, "hdr-not-ready", reason +
            " PaRuX did not change Windows HDR settings or start the game.");
    }
}
