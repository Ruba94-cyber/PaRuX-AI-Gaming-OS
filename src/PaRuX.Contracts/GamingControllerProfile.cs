namespace PaRuX.Contracts;

/// <summary>
/// User-tunable controller diagnostics/mapping profile. It is data only: PaRuX applies it to the
/// values it reports and to the Console navigation input, and never injects it into games or
/// into a virtual controller driver. Defaults match the XInput reference deadzones.
/// </summary>
public sealed record GamingControllerProfile
{
    public const int DefaultLeftDeadzone = 7849;
    public const int DefaultRightDeadzone = 8689;
    public const int DefaultTriggerThreshold = 30;
    public const int MaxDeadzone = 20000;

    /// <summary>Every XInput button bit a remap may use (one bit each).</summary>
    public static readonly IReadOnlyList<ushort> RemappableButtons = new ushort[]
    {
        0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080,
        0x0100, 0x0200, 0x1000, 0x2000, 0x4000, 0x8000,
    };

    public int LeftDeadzone { get; init; } = DefaultLeftDeadzone;
    public int RightDeadzone { get; init; } = DefaultRightDeadzone;
    /// <summary>Raw 0..255 trigger value below which a trigger reads as released.</summary>
    public int TriggerThreshold { get; init; } = DefaultTriggerThreshold;
    public bool InvertLeftY { get; init; }
    public bool InvertRightY { get; init; }
    /// <summary>Source button bit to destination button bit. Empty means identity.</summary>
    public Dictionary<ushort, ushort> ButtonRemap { get; init; } = new();

    public static GamingControllerProfile Default { get; } = new();

    /// <summary>Returns null when valid, otherwise a short reason. Never throws.</summary>
    public string? Validate()
    {
        if (LeftDeadzone is < 0 or > MaxDeadzone) return "Left deadzone is out of range.";
        if (RightDeadzone is < 0 or > MaxDeadzone) return "Right deadzone is out of range.";
        if (TriggerThreshold is < 0 or > 254) return "Trigger threshold is out of range.";
        if (ButtonRemap is null) return "Button remap is missing.";
        if (ButtonRemap.Count > RemappableButtons.Count) return "Too many button remaps.";
        var destinations = new HashSet<ushort>();
        foreach (var (source, destination) in ButtonRemap)
        {
            if (!RemappableButtons.Contains(source) || !RemappableButtons.Contains(destination))
                return "Only single, known XInput buttons can be remapped.";
            if (!destinations.Add(destination))
                return "Two buttons cannot be remapped to the same destination.";
        }
        return null;
    }
}
