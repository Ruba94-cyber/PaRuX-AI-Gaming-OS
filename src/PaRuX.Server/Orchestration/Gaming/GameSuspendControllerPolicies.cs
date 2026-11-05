using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Server.Orchestration.Gaming;

public enum AntiCheatAssessment { Unknown, ConfirmedPresent, VerifiedAbsent }
public enum GameSuspendTrigger { None, FocusLost, LowBattery, HighTemperature }

public sealed record GameAutoSuspendRule(
    string GameId, bool Enabled, bool SuspendOnFocusLoss,
    int? BatteryBelowPercent, double? TemperatureAtLeastCelsius);

public sealed record GameAutoSuspendDecision(
    bool Eligible, GameSuspendTrigger Trigger, string Reason);

/// <summary>
/// Pure proposal. The existing QuickResumeService must STILL verify PID+start time,
/// session rights, safe process and ownership before changing a process state.
/// Unknown anti-cheat status NEVER authorizes an automatic suspend.
/// </summary>
public static class GameAutoSuspendPolicy
{
    private static readonly HashSet<string> ProtectedAntiCheatProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64",
            "vgc", "FACEITService", "EAAntiCheat"
        };

    public static bool IsExcludedProcess(string? processName,
        IReadOnlyCollection<string>? userExcludedProcesses = null)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        string normalized = processName.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring(0, normalized.Length - 4);
        return ProtectedAntiCheatProcesses.Contains(normalized)
            || (userExcludedProcesses?.Any(x =>
                string.Equals(x?.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
                    ? x.Trim().Substring(0, x.Trim().Length - 4) : x?.Trim(),
                    normalized, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    public static GameAutoSuspendDecision Evaluate(
        GameAutoSuspendRule? rule, bool processIdentityVerified, AntiCheatAssessment antiCheat,
        bool focusLost, int? batteryPercent, double? temperatureCelsius)
    {
        if (rule is null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.GameId))
            return new(false, GameSuspendTrigger.None, "Automatic suspend is disabled for this game.");
        if (!processIdentityVerified || antiCheat != AntiCheatAssessment.VerifiedAbsent)
            return new(false, GameSuspendTrigger.None,
                "Verify the process identity and absence of anti-cheat before proposing suspend.");
        if (rule.SuspendOnFocusLoss && focusLost)
            return new(true, GameSuspendTrigger.FocusLost, "Game lost focus; suspend can be offered.");
        if (rule.BatteryBelowPercent is int threshold && threshold is >= 1 and <= 100
            && batteryPercent is int charge && charge >= 0 && charge < threshold)
            return new(true, GameSuspendTrigger.LowBattery, "Battery below the selected threshold.");
        if (rule.TemperatureAtLeastCelsius is double thermal
            && double.IsFinite(thermal) && thermal is >= 50 and <= 100
            && temperatureCelsius is double measured && double.IsFinite(measured) && measured >= thermal)
            return new(true, GameSuspendTrigger.HighTemperature, "Temperature reached the selected threshold.");
        return new(false, GameSuspendTrigger.None, "No enabled suspend condition has been met.");
    }
}

public enum ControllerCurve { Linear, Precision, Aggressive }

/// <summary>Simple, user-editable test panel state, not a virtual controller implementation.</summary>
public sealed record ControllerTestSnapshot(
    IReadOnlyDictionary<string, double> Axes,
    IReadOnlyList<string> PressedButtons, double? MeasuredLatencyMs,
    string DeviceStatus);

public sealed record ControllerRemapPreview(
    string GameId, double Deadzone, ControllerCurve Curve,
    IReadOnlyDictionary<string, string> ButtonMapping);

public static class ControllerRemapPresenter
{
    public static string? Validate(ControllerRemapPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (string.IsNullOrWhiteSpace(preview.GameId)
            || !double.IsFinite(preview.Deadzone) || preview.Deadzone < 0 || preview.Deadzone > 0.6)
            return "Choose a game and a deadzone from 0 to 0.6.";
        if (!Enum.IsDefined(typeof(ControllerCurve), preview.Curve))
            return "Unknown response curve.";
        if (preview.ButtonMapping is null || preview.ButtonMapping.Count > 32
            || preview.ButtonMapping.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)))
            return "Invalid button mapping.";
        if (preview.ButtonMapping.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != preview.ButtonMapping.Count)
            return "Two inputs cannot map to one destination.";
        return null;
    }

    /// <summary>Preview only. Does not send input, install drivers or touch XInput state.</summary>
    public static double PreviewAxis(double raw, ControllerRemapPreview preview)
    {
        if (Validate(preview) is not null) throw new ArgumentException("Invalid profile.", nameof(preview));
        if (!double.IsFinite(raw)) throw new ArgumentOutOfRangeException(nameof(raw));
        double magnitude = Math.Clamp(Math.Abs(raw), 0, 1);
        if (magnitude <= preview.Deadzone) return 0;
        double scaled = (magnitude - preview.Deadzone) / (1 - preview.Deadzone);
        double curved = preview.Curve switch
        {
            ControllerCurve.Precision => scaled * scaled,
            ControllerCurve.Aggressive => Math.Sqrt(scaled),
            _ => scaled
        };
        return Math.CopySign(curved, raw);
    }

    public static string LatencyText(ControllerTestSnapshot snapshot)
    {
        if (snapshot.MeasuredLatencyMs is not double ms || !double.IsFinite(ms) || ms < 0)
            return "Latency not measured on real hardware.";
        return ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            + " ms (measured locally)";
    }
}

/// <summary>Only PaRuX-owned changes may be reversed; user-managed drivers stay installed.</summary>
public static class ControllerUndoEverythingPlanner
{
    public static IReadOnlyList<string> Steps(bool explicitConsent) =>
        !explicitConsent ? Array.Empty<string>() : new[]
        {
            "Stop PaRuX-owned virtual-pad forwarding",
            "Invoke HidHideManager.RestoreAsync for PaRuX-owned journal entries",
            "Clear PaRuX active per-game mapping override",
            "Refresh device status and report any incomplete recovery"
        };
}
