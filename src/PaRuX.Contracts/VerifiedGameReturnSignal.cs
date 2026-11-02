using System.Text.Json;

namespace PaRuX.Contracts;

/// <summary>
/// An event emitted only after a version-matched, directly owned game process
/// is observed to exit AND the Core confirms AI resources are available.
/// Stored in the existing machine-local state folder so the already-running
/// FileSystemWatcher can notify Console without GPU/CPU polling.
/// Never stores PID, game title, image path, HWND or any machine secret.
/// </summary>
public sealed record VerifiedGameReturnSignal(long SessionVersion, DateTime ExitUtc)
{
    public const string FileName = "verified-game-return.json";
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);

    public static string FileIn(string stateDirectory) => Path.Combine(stateDirectory, FileName);

    public static bool IsRecentAndMatching(VerifiedGameReturnSignal? signal,
        DateTime? confirmedExitUtc, DateTime nowUtc)
    {
        if (signal is null || signal.SessionVersion < 1 ||
            confirmedExitUtc is null || signal.ExitUtc.Kind != DateTimeKind.Utc ||
            nowUtc < signal.ExitUtc || nowUtc - signal.ExitUtc > MaximumAge)
            return false;
        // The exact Core-confirmed timestamp is our event identity. A stale or
        // user-created marker cannot substitute for a verified process exit.
        return signal.ExitUtc == confirmedExitUtc.Value.ToUniversalTime();
    }

    public static bool TryRead(string path, out VerifiedGameReturnSignal? signal)
    {
        signal = null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024) return false;
            signal = JsonSerializer.Deserialize<VerifiedGameReturnSignal>(File.ReadAllText(path));
            return signal is { SessionVersion: > 0 } && signal.ExitUtc.Kind == DateTimeKind.Utc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static void Publish(string path, VerifiedGameReturnSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.SessionVersion < 1 || signal.ExitUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A verified UTC session exit is required.", nameof(signal));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(signal));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

    
/// <summary>Pure permission gate for any automatic foreground activation.
/// Every condition must be true; no mere focus event or removed Gaming marker
/// can authorize bringing Console Home over another application.</summary>
public static class ConsoleAutoReturnPolicy
{
    public static bool CanActivate(bool enabled, bool consoleIsOpen,
        bool interactionBusy, bool gamingActive, GamingStatusDto? status,
        VerifiedGameReturnSignal? signal, DateTime nowUtc) =>
        enabled && consoleIsOpen && !interactionBusy && !gamingActive &&
        GameLibraryForegroundReturnPolicy.HasConfirmedOwnedExit(status) &&
        VerifiedGameReturnSignal.IsRecentAndMatching(
            signal, status?.LastVerifiedOwnedGameExitUtc, nowUtc);
}
