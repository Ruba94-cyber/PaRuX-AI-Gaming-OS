namespace PaRuX.Contracts;

/// <summary>Read-only status of PaRuX-owned controller-isolation recovery; exposes no HID device IDs.</summary>
public sealed record HidHideHealthDto(bool Available, bool RecoveryRequired, string Message);
