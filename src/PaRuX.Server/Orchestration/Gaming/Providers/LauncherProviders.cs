using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public sealed class PlayniteLauncherProvider : IGameLauncherProvider
{
    private readonly object _processGate = new();
    private readonly Func<Process[]> _getRunningProcesses;
    private Process? _startedProcess;

    public PlayniteLauncherProvider(Func<Process[]>? getRunningProcesses = null) =>
        _getRunningProcesses = getRunningProcesses ?? (() => Process.GetProcessesByName("Playnite.FullscreenApp")
            .Concat(Process.GetProcessesByName("Playnite.DesktopApp")).ToArray());

    public string Id => "playnite";
    public string Name => "Playnite Launcher (Fullscreen)";

    public bool IsInstalled => !string.IsNullOrEmpty(ExecutablePath);

    public string? ExecutablePath
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] candidates =
            {
                Path.Combine(localAppData, "Playnite", "Playnite.FullscreenApp.exe"),
                Path.Combine(localAppData, "Playnite", "Playnite.DesktopApp.exe"),
                Path.Combine(programFiles, "Playnite", "Playnite.FullscreenApp.exe"),
                Path.Combine(programFiles, "Playnite", "Playnite.DesktopApp.exe"),
                Path.Combine(programFilesX86, "Playnite", "Playnite.FullscreenApp.exe")
            };

            return candidates.FirstOrDefault(CustomExecutableInputProvider.IsSafeHelperPath);
        }
    }

    // A similarly named process does not establish that this is the selected
    // Playnite installation. Allow Desktop/Fullscreen peers only within the
    // same checked directory; do not adopt or terminate them.
    private static bool MatchesSelectedInstallation(string? runningPath, string selectedPath)
    {
        if (!CustomExecutableInputProvider.IsSafeHelperPath(runningPath)) return false;
        string candidate = Path.GetFullPath(runningPath!);
        if (string.Equals(candidate, Path.GetFullPath(selectedPath), StringComparison.OrdinalIgnoreCase))
            return true;
        string runningName = Path.GetFileName(candidate);
        string selectedName = Path.GetFileName(selectedPath);
        bool isOfficialPair =
            (string.Equals(runningName, "Playnite.DesktopApp.exe", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(runningName, "Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase)) &&
            (string.Equals(selectedName, "Playnite.DesktopApp.exe", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(selectedName, "Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase));
        return isOfficialPair && string.Equals(Path.GetDirectoryName(candidate),
            Path.GetDirectoryName(Path.GetFullPath(selectedPath)), StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(false);
        // An explicit, validated path chosen in the profile outranks auto-detection.
        string? exe = profile.LauncherSettings.TryGetValue("path", out string? customPath) &&
            CustomExecutableInputProvider.IsSafeHelperPath(customPath)
            ? customPath
            : ExecutablePath;
        if (exe == null)
            return Task.FromResult(false);

        lock (_processGate)
        {
            try
            {
                if (_startedProcess != null)
                {
                    if (!_startedProcess.HasExited)
                        return Task.FromResult(MatchesSelectedInstallation(
                            _startedProcess.StartInfo.FileName, exe));
                    _startedProcess.Dispose();
                    _startedProcess = null;
                }

                Process[] existing = _getRunningProcesses();
                bool found = existing.Length > 0;
                try
                {
                    // Do not claim success based on the process name alone.
                    // If any candidate cannot be verified, fail rather than
                    // launching a duplicate or taking ownership of it.
                    foreach (Process process in existing)
                    {
                        string? runningPath = process.MainModule?.FileName;
                        if (!MatchesSelectedInstallation(runningPath, exe))
                            return Task.FromResult(false);
                    }
                }
                finally
                {
                    foreach (Process process in existing) process.Dispose();
                }
                if (found) return Task.FromResult(true);

                ct.ThrowIfCancellationRequested();
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = true
                };
                if (!exe.EndsWith("Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add("--fullscreen");

                Process? launched = Process.Start(psi);
                if (launched == null) return Task.FromResult(false);
                _startedProcess = launched;
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or InvalidOperationException or
                OperationCanceledException or ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                return Task.FromResult(false);
            }
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
    {
        // Never close Playnite or a user's game; only forget our exact handle.
        // Serialize with Start so a late launch cannot survive Stop unnoticed.
        lock (_processGate)
        {
            _startedProcess?.Dispose();
            _startedProcess = null;
            return Task.FromResult(true);
        }
    }
}

public sealed class SteamBigPictureLauncherProvider : IGameLauncherProvider
{
    public string Id => "steam";
    public string Name => "Steam Big Picture";

    public bool IsInstalled => !string.IsNullOrEmpty(ExecutablePath);

    public string? ExecutablePath
    {
        get
        {
            return SteamInstallLocator.FindExecutable();
        }
    }

    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
    {
        // Use the local path-validated Steam executable, not the system-wide
        // steam:// protocol handler (which can be reassigned). This does not
        // independently authenticate the publisher or verify a code signature.
        if (ct.IsCancellationRequested) return Task.FromResult(false);
        string? steamExe = ExecutablePath;
        if (string.IsNullOrWhiteSpace(steamExe)) return Task.FromResult(false);

        try
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo
            {
                FileName = steamExe,
                WorkingDirectory = Path.GetDirectoryName(steamExe)!,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-bigpicture");
            using Process? request = Process.Start(psi);
            return Task.FromResult(request != null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            ArgumentException or InvalidOperationException or NotSupportedException or
            System.Security.SecurityException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
    {
        return Task.FromResult(true);
    }
}

public sealed class CustomExecutableLauncherProvider : IGameLauncherProvider
{
    public string Id => "custom";
    public string Name => "Custom Executable Launcher";

    public bool IsInstalled => true;

    public string? ExecutablePath => null;

    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (ct.IsCancellationRequested ||
            !profile.LauncherSettings.TryGetValue("path", out string? exePath) ||
            !CustomExecutableInputProvider.IsSafeHelperPath(exePath))
            return Task.FromResult(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            };

            if (profile.LauncherSettings.TryGetValue("args", out string? args) && !string.IsNullOrWhiteSpace(args))
            {
                psi.Arguments = args;
            }

            ct.ThrowIfCancellationRequested();
            using Process? launched = Process.Start(psi);
            return Task.FromResult(launched != null);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}

public sealed class NoneLauncherProvider : IGameLauncherProvider
{
    public string Id => "none";
    public string Name => "No Launcher (Manage Game Directly)";
    public bool IsInstalled => true;
    public string? ExecutablePath => null;
    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}
