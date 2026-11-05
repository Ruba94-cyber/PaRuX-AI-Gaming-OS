using System.Diagnostics;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Windows-only conservative check that a user-selected game's *specific*
/// executable is not running before a game mod ZIP is placed or removed.
/// A closed-game checkbox alone is never treated as OS process evidence.
///
/// This proves only the selected executable's absence at the point of each
/// check, NOT the absence of differently named child/helper processes or
/// anti-cheat clients. An inaccessible same-name process fails closed.
/// </summary>
internal static class GameModProcessGuard
{
    internal static string ValidateExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select the actual game .exe, not a launcher, shortcut or network path.");

        string full = Path.GetFullPath(path);
        string name = Path.GetFileNameWithoutExtension(full);
        if (GameAutoSuspendPolicy.IsExcludedProcess(name))
            throw new InvalidOperationException("A system or security executable cannot be used as a mod target.");
        var file = new FileInfo(full);
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !ManualGameLibrary.IsSafeDirectory(Path.GetDirectoryName(full)!))
            throw new InvalidOperationException("The selected game executable is missing or linked.");
        return full;
    }

    internal static bool IsRunning(string validFullPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Game process verification requires Windows.");
        string name = Path.GetFileNameWithoutExtension(validFullPath);
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Cannot inspect the game's process state.", ex);
        }
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    string observed = process.MainModule?.FileName
                        ?? throw new InvalidOperationException("Cannot inspect the game process image.");
                    if (string.Equals(Path.GetFullPath(observed), validFullPath,
                        StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException("Cannot verify the game process identity.", ex);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Do not claim 'closed' if a same-named process is
                    // inaccessible due to permissions or anti-cheat.
                    throw new InvalidOperationException(
                        "A same-named running process cannot be verified; close the game or run with adequate permissions.", ex);
                }
                catch (InvalidOperationException)
                {
                    // A process that exited between the initial check and
                    // module inspection can be ignored; all other failures
                    // are surfaced below.
                    if (!process.HasExited)
                        throw;
                }
            }
            return false;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }
}
