namespace PaRuX.Contracts;

/// <summary>Read-only current Windows display configuration, without EDID serials or HDR claims.</summary>
public sealed record GamingDisplayModeDto(
    string DisplayId,
    bool Primary,
    int X,
    int Y,
    int Width,
    int Height,
    int RefreshRateHz);

public sealed record GamingDisplaySnapshotDto(
    bool Supported,
    string Message,
    IReadOnlyList<GamingDisplayModeDto> Displays);
