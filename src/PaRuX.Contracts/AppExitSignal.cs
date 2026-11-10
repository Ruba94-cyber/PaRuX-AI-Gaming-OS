using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Contracts;

/// <summary>
/// A deterministic way to close the PaRuX Desktop that belongs to one data folder:
/// <c>PaRuX.exe --exit</c> signals the running instance that uses the same data root and returns.
///
/// The tray menu is the interactive exit, but its icon is owned by Explorer and cannot be tied to
/// one process, so scripts (IT deployment, replacing a portable folder, acceptance tests) had no
/// safe way to ask a specific PaRuX to shut down cleanly instead of killing it.
/// The signal carries no data and only reaches processes of the same Windows session.
/// </summary>
public static class AppExitSignal
{
    public const string ExitArgument = "--exit";

    /// <summary>True when the command line (first element is the executable) asks for an exit.</summary>
    public static bool IsExitRequest(IReadOnlyList<string>? commandLine)
    {
        if (commandLine == null) return false;
        for (int i = 1; i < commandLine.Count; i++)
            if (string.Equals(commandLine[i], ExitArgument, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Session-local event name derived from the data root, so a portable copy on a
    /// USB stick and an installed PaRuX never close each other.</summary>
    public static string NameFor(string dataRoot)
    {
        string normalized = Path.GetFullPath(dataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return "Local\\PaRuX.App.Exit." + scope;
    }

    /// <summary>Signals the running instance. False when none is listening.</summary>
    [SupportedOSPlatform("windows")]
    public static bool TrySignal(string name)
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Starts listening; <paramref name="onExitRequested"/> runs once on a pool thread.
    /// Dispose the result to stop listening.</summary>
    [SupportedOSPlatform("windows")]
    public static IDisposable Listen(string name, Action onExitRequested)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            handle, (_, timedOut) => { if (!timedOut) onExitRequested(); }, null, Timeout.Infinite, executeOnlyOnce: true);
        return new Listener(handle, registration);
    }

    private sealed class Listener(EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }
    }
}
