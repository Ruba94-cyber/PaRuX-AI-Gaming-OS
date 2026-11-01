namespace PaRuX.Contracts;

/// <summary>
/// On-demand controller diagnostics; no persisted input or personal device IDs.
/// Values are normalized to -1..1 for sticks and 0..1 for triggers.
/// </summary>
public sealed record GamingControllerStateDto(
    int Slot,
    ushort Buttons,
    float LeftX,
    float LeftY,
    float RightX,
    float RightY,
    float LeftTrigger,
    float RightTrigger);
