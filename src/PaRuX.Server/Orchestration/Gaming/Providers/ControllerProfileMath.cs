using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

/// <summary>Pure, hardware-free profile application and idle calibration.</summary>
internal static class ControllerProfileMath
{
    internal readonly record struct RawPad(
        ushort Buttons, byte LeftTrigger, byte RightTrigger, short LeftX, short LeftY, short RightX, short RightY);

    internal static GamingControllerStateDto Apply(int slot, RawPad raw, GamingControllerProfile profile)
    {
        var (lx, ly) = NativeXInputProbe.NormalizeStick(raw.LeftX, raw.LeftY, profile.LeftDeadzone);
        var (rx, ry) = NativeXInputProbe.NormalizeStick(raw.RightX, raw.RightY, profile.RightDeadzone);
        if (profile.InvertLeftY) ly = -ly;
        if (profile.InvertRightY) ry = -ry;
        return new GamingControllerStateDto(slot, RemapButtons(raw.Buttons, profile.ButtonRemap), lx, ly, rx, ry,
            Trigger(raw.LeftTrigger, profile.TriggerThreshold), Trigger(raw.RightTrigger, profile.TriggerThreshold));
    }

    private static float Trigger(byte raw, int threshold) =>
        raw <= threshold ? 0f : (raw - threshold) / (255f - threshold);

    internal static ushort RemapButtons(ushort buttons, IReadOnlyDictionary<ushort, ushort> remap)
    {
        if (remap.Count == 0) return buttons;
        int result = buttons;
        foreach (ushort source in remap.Keys) result &= ~source;
        foreach (var (source, destination) in remap)
            if ((buttons & source) != 0) result |= destination;
        return (ushort)result;
    }

    /// <summary>
    /// Recommends a stick deadzone from samples taken while the stick rests untouched: the largest
    /// observed drift with 25% headroom plus a small margin, clamped to a sane range. Too few samples
    /// return null instead of guessing.
    /// </summary>
    internal static int? RecommendDeadzone(IReadOnlyCollection<(short X, short Y)> idleSamples)
    {
        if (idleSamples.Count < 10) return null;
        double worst = idleSamples.Max(s => Math.Sqrt((double)s.X * s.X + (double)s.Y * s.Y));
        int value = (int)Math.Ceiling(worst * 1.25) + 500;
        return Math.Clamp(value, 2000, GamingControllerProfile.MaxDeadzone);
    }
}
