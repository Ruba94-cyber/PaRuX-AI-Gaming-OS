namespace PaRuX.Contracts;

/// <summary>Requests a resolution / refresh-rate change for one monitor reported by the topology snapshot.</summary>
public sealed record GamingDisplayModeChangeRequest(string DisplayId, int Width, int Height, int RefreshRateHz);

/// <summary>
/// State of the single reversible display change. A change is always temporary until confirmed; if it
/// is not confirmed before <see cref="SecondsRemaining"/> reaches zero PaRuX restores the previous mode.
/// </summary>
public sealed record GamingDisplayChangeStatusDto(
    bool Pending,
    string? DisplayId,
    int SecondsRemaining,
    string Message);

/// <summary>One resolution / refresh-rate pair the display driver lists for a monitor.</summary>
public sealed record GamingDisplayModeOptionDto(int Width, int Height, int RefreshRateHz);
