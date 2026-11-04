namespace PaRuX.Contracts;

/// <summary>Owner-confirmed request to hide ONE physical pad from games. The Core proves the HID binding itself.</summary>
public sealed record VirtualPadIsolateRequest(
    ushort VendorId, ushort ProductId, int SameIdentityControllers, bool Consent, string AppExecutable);

/// <summary>InstancePath is set only when the controller-to-HID binding was proven and the pad was hidden.</summary>
public sealed record VirtualPadIsolateResult(bool Success, string Message, string? InstancePath, bool BindingProven);

public sealed record VirtualPadIsolationState(bool Isolated);
