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

    public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(false);
        string? exe = ExecutablePath;
        if (exe == null)
        {
            // If explicit path is passed in settings, try it
            if (profile.LauncherSettings.TryGetValue("path", out string? customPath) &&
                CustomExecutableInputProvider.IsSafeHelperPath(customPath))
            {
                exe = customPath;
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        try
        {
            // Check if Playnite is already running
            Process[] existing = _getRunningProcesses();
            bool alreadyRunning = existing.Length > 0;
            foreach (Process process in existing) process.Dispose();
            if (alreadyRunning) return Task.FromResult(true);
            ct.ThrowIfCancellationRequested();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            };

            if (!exe.EndsWith("Playnite.FullscreenApp.exe", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("--fullscreen");
            }

            Process? started = Process.Start(psi);
            if (started == null) return Task.FromResult(false);
            lock (_processGate)
            {
                _startedProcess?.Dispose();
                _startedProcess = started;
            }
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
    {
        // Returning to AI is NOT permission to close the user's launcher or any games.
        // Releasing our handle is safe; Playnite stays under the user's control.
        Process? started;
        lock (_processGate)
        {
            started = _startedProcess;
            _startedProcess = null;
        }
        started?.Dispose();
        return Task.FromResult(true);
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
