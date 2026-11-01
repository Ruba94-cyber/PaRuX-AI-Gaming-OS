using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public sealed class NativeInputProvider : IInputProvider
{
    public string Id => "native";
    // Provider enumeration must never poll controller hardware. Live slots are
    // queried only by the explicit desktop-owner diagnostics endpoint.
    public string Name => "Native Controller Input";
    public bool IsInstalled => true;
    public string? ExecutablePath => null;
    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}

public sealed class XOutputInputProvider : IInputProvider
{
    private readonly object _processGate = new();
    private readonly Func<Process[]> _getRunningProcesses;
    private Process? _startedProcess;

    public XOutputInputProvider(Func<Process[]>? getRunningProcesses = null) =>
        _getRunningProcesses = getRunningProcesses ?? (() => Process.GetProcessesByName("XOutput"));

    public string Id => "xoutput";
    public string Name => "XOutput Controller Mapper";

    public bool IsInstalled => !string.IsNullOrEmpty(ExecutablePath);

    public string? ExecutablePath
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string[] candidates =
            {
                Path.Combine(localAppData, "XOutput", "XOutput.exe"),
                Path.Combine(programFiles, "XOutput", "XOutput.exe"),
                @"C:\XOutput\XOutput.exe"
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct)
    {
        // A remote stream may already expose a virtual controller through Sunshine.
        // Refuse a second mapper rather than returning fake success or double input.
        if (GamingInputSafetyPolicy.Validate(profile) != null)
            return Task.FromResult(false);

        string? exe = ExecutablePath;
        if (exe == null)
        {
            if (profile.InputSettings.TryGetValue("path", out string? customPath) && File.Exists(customPath))
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
            Process[] existing = _getRunningProcesses();
            bool alreadyRunning = existing.Length > 0;
            foreach (Process process in existing) process.Dispose();
            if (alreadyRunning) return Task.FromResult(true);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            };
            if (profile.InputSettings.TryGetValue("args", out string? args) && !string.IsNullOrWhiteSpace(args))
            {
                psi.Arguments = args;
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
        Process? ownedProcess;
        lock (_processGate)
        {
            ownedProcess = _startedProcess;
            _startedProcess = null;
        }

        if (ownedProcess == null) return Task.FromResult(true);

        try
        {
            // A helper started by PaRuX may be asked to exit gracefully; forcibly
            // killing the input stack is forbidden without an explicit separate grant.
            if (ownedProcess.HasExited)
            {
                ownedProcess.Dispose();
                return Task.FromResult(true);
            }
            if (ownedProcess.CloseMainWindow() && ownedProcess.WaitForExit(1000))
            {
                ownedProcess.Dispose();
                return Task.FromResult(true);
            }
        }
        catch { }

        // Preserve the handle for a future retry. Report failure instead of claiming
        // input was released or silently terminating an unresponsive process.
        lock (_processGate) _startedProcess ??= ownedProcess;
        return Task.FromResult(false);
    }
}

public sealed class SunshineInputProvider : IInputProvider
{
    public string Id => "sunshine";
    public string Name => "Sunshine Virtual Controller Input";
    public bool IsInstalled => SunshineSessionTriggerProvider.IsInstalledOnMachine;
    public string? ExecutablePath => SunshineSessionTriggerProvider.ExecutablePath;
    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) =>
        Task.FromResult(GamingInputSafetyPolicy.Validate(profile) == null);
    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
}

public sealed class CustomExecutableInputProvider : IInputProvider
{
    private readonly object _processGate = new();
    private Process? _ownedProcess;

    public string Id => "custom";
    public string Name => "Custom Input Utility";

    public bool IsInstalled => true;
    public string? ExecutablePath => null;

    // A custom input helper is always an explicitly selected, local Windows
    // executable. Do not follow shortcuts, network paths or reparse-point files.
    internal static bool IsSafeHelperPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(path);
            return string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullPath) &&
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || GamingInputSafetyPolicy.Validate(profile) != null ||
            !profile.InputSettings.TryGetValue("path", out string? path) || !IsSafeHelperPath(path))
            return Task.FromResult(false);

        lock (_processGate)
        {
            // If a previous helper could not be stopped, never spawn a duplicate
            // controller mapper. A stale, exited handle can be safely reclaimed.
            if (_ownedProcess != null)
            {
                try
                {
                    if (!_ownedProcess.HasExited) return Task.FromResult(false);
                    _ownedProcess.Dispose();
                    _ownedProcess = null;
                }
                catch { return Task.FromResult(false); }
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var psi = new ProcessStartInfo
                {
                    FileName = path!,
                    WorkingDirectory = Path.GetDirectoryName(path!)!,
                    UseShellExecute = false
                };
                if (profile.InputSettings.TryGetValue("args", out string? args) &&
                    !string.IsNullOrWhiteSpace(args))
                    psi.Arguments = args;

                Process? started = Process.Start(psi);
                if (started == null) return Task.FromResult(false);

                // PaRuX only owns this exact process handle. We never discover,
                // terminate or claim ownership of unrelated mapper processes.
                _ownedProcess = started;
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                InvalidOperationException or ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                return Task.FromResult(false);
            }
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
    {
        lock (_processGate)
        {
            if (_ownedProcess == null) return Task.FromResult(true);
            try
            {
                if (_ownedProcess.HasExited ||
                    (_ownedProcess.CloseMainWindow() && _ownedProcess.WaitForExit(1000)))
                {
                    _ownedProcess.Dispose();
                    _ownedProcess = null;
                    return Task.FromResult(true);
                }
            }
            catch { /* No unverified cleanup success or force-kill. */ }

            // Preserve the owned handle for a later retry. A headless helper
            // that refuses graceful shutdown is a real Gaming recovery failure.
            return Task.FromResult(false);
        }
    }
}
