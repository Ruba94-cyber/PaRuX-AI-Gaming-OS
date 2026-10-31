namespace PaRuX.Contracts;

/// <summary>
/// Result of an explicit desktop-owner launch from the existing manual library.
/// Monitoring is true only when PaRuX verified the created process identity.
/// No executable path or machine-private details are returned.
/// </summary>
public sealed record ManualGameLaunchResult(
    bool Launched,
    bool AutomaticReturnOnExit,
    string Message);
