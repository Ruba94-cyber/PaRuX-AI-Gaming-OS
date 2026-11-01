namespace PaRuX.Contracts;

/// <summary>What a manual library entry is. "game" prepares Gaming Mode before launch; "application"
/// (tools, front-ends, launchers, any other program) is started as-is and never changes AI state.</summary>
public static class ManualEntryKinds
{
    public const string Game = "game";
    public const string Application = "application";
    public static bool IsValid(string? kind) => kind is Game or Application;
}

/// <summary>
/// A saved local executable. <paramref name="Arguments"/> is passed to that program verbatim (no shell is
/// involved); <paramref name="WorkingDirectory"/> defaults to the executable's folder when empty.
/// Older library files without these fields load as plain games.
/// </summary>
public sealed record ManualGameDto(
    string Id,
    string Name,
    string ExecutablePath,
    string Arguments = "",
    string WorkingDirectory = "",
    string Kind = ManualEntryKinds.Game);

public sealed record AddManualGameRequest(
    string Name,
    string ExecutablePath,
    string Arguments = "",
    string WorkingDirectory = "",
    string Kind = ManualEntryKinds.Game);
