namespace PaRuX.Contracts;

/// <summary>How far a launch request is known to have progressed. "requested" only means a
/// request was handed to a launcher PaRuX does not own; it is never proof that a game is running.</summary>
public static class GameStartStates
{
    public const string None = "none";
    public const string Requested = "requested";
    public const string ProcessStarted = "process-started";
    public const string Verified = "verified";
}

/// <summary>One-shot positioning outcome, not a guarantee that an emulator
/// renderer or exclusive fullscreen game obeyed a monitor preference.</summary>
public static class GameWindowPlacementStates
{
    public const string NotRequested = "";
    public const string Placed = "placed";
    public const string AlreadyOnTv = "already-on-tv";
    public const string NotConfirmed = "not-confirmed";
    public const string UnsupportedWindow = "unsupported-window";
    public const string DisplayUnavailable = "display-unavailable";
}

/// <summary>
/// Result of an explicit desktop-owner launch from the existing manual library.
/// Monitoring is true only when PaRuX verified the created process identity.
/// <see cref="StartState"/> separates a request to a launcher from a process PaRuX started and from a
/// process whose exact identity (PID + creation time + image) was verified.
/// No executable path or machine-private details are returned.
/// </summary>
public sealed record ManualGameLaunchResult(
    bool Launched,
    bool AutomaticReturnOnExit,
    string Message)
{
    private readonly string? _startState;

    public string StartState
    {
        get => _startState ?? (!Launched ? GameStartStates.None
            : AutomaticReturnOnExit ? GameStartStates.Verified : GameStartStates.Requested);
        init => _startState = value;
    }

    /// <summary>Optional, one-shot TV placement result for a windowed, directly
    /// owned game. Empty means no routing was requested (legacy Core behavior).
    /// "placed" is not a guarantee of emulator exclusive-fullscreen output.</summary>
    public string WindowPlacement { get; init; } = string.Empty;
}
