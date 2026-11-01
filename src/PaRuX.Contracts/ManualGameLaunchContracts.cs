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
}
