namespace PaRuX.Contracts;

/// <summary>Read-only current Windows display configuration, without EDID serials or HDR claims.</summary>
public sealed record GamingDisplayModeDto(
    string DisplayId,
    bool Primary,
    int X,
    int Y,
    int Width,
    int Height,
    int RefreshRateHz)
{
    /// <summary>Optional, Windows-reported friendly device description only.
    /// No EDID serial, hardware instance ID or registry key is transmitted.</summary>
    public string? FriendlyName { get; init; }
}

public sealed record GamingDisplaySnapshotDto(
    bool Supported,
    string Message,
    IReadOnlyList<GamingDisplayModeDto> Displays);
