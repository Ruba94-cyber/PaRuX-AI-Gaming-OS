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

            return candidates.FirstOrDefault(CustomExecutableInputProvider.IsSafeHelperPath);
        }
    }

    public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct)
    {
        // Never start a second local mapper next to remote Sunshine input.
        if (ct.IsCancellationRequested || GamingInputSafetyPolicy.Validate(profile) != null)
            return Task.FromResult(false);

        // An explicit, validated path chosen in the profile outranks auto-detection.
        string? exe = profile.InputSettings.TryGetValue("path", out string? customPath) &&
            CustomExecutableInputProvider.IsSafeHelperPath(customPath)
            ? customPath
            : ExecutablePath;
        if (exe == null)
            return Task.FromResult(false);

        lock (_processGate)
        {
            // Serialize Start/Stop against the SAME owned handle. Previous code
            // could start a duplicate while another thread had taken ownership
            // of the first helper, then dispose the old handle without closing it.
            if (_startedProcess != null)
            {
                try
                {
                    if (!_startedProcess.HasExited)
                    {
                        bool sameFile = string.Equals(_startedProcess.StartInfo.FileName,
                            exe, StringComparison.OrdinalIgnoreCase);
                        string expectedArgs = profile.InputSettings.TryGetValue("args", out string? args)
                            ? args ?? string.Empty : string.Empty;
                        return Task.FromResult(sameFile &&
                            string.Equals(_startedProcess.StartInfo.Arguments, expectedArgs,
                                StringComparison.Ordinal));
                    }
                    _startedProcess.Dispose();
                    _startedProcess = null;
                }
                catch { return Task.FromResult(false); }
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                Process[] existing = _getRunningProcesses();
                bool externallyRunning = false;
                bool unverifiedMapper = false;
                try
                {
                    foreach (Process process in existing)
                    {
                        // A same-name process alone is not proof it is the
                        // configured mapper. Never claim success for an unknown
                        // process or add another controller on top of it.
                        try
                        {
                            string? runningPath = process.MainModule?.FileName;
                            if (runningPath != null && string.Equals(
                                    Path.GetFullPath(runningPath), Path.GetFullPath(exe),
                                    StringComparison.OrdinalIgnoreCase))
                                externallyRunning = true;
                            else
                                unverifiedMapper = true;
                        }
                        catch { unverifiedMapper = true; }
                    }
                }
                finally
                {
                    foreach (Process process in existing) process.Dispose();
                }
                if (unverifiedMapper) return Task.FromResult(false);
                if (externallyRunning) return Task.FromResult(true);
                ct.ThrowIfCancellationRequested();

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = false
                };
                if (profile.InputSettings.TryGetValue("args", out string? extraArgs) &&
                    !string.IsNullOrWhiteSpace(extraArgs))
                    psi.Arguments = extraArgs;

                Process? started = Process.Start(psi);
                if (started == null) return Task.FromResult(false);
                _startedProcess = started;
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }
    }

    public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct)
    {
        lock (_processGate)
        {
            if (_startedProcess == null) return Task.FromResult(true);
            if (ct.IsCancellationRequested) return Task.FromResult(false);

            try
            {
                // Only the exact helper launched by PaRuX can be closed. Never
                // terminate an externally owned helper or force-kill a process.
                if (_startedProcess.HasExited ||
                    (_startedProcess.CloseMainWindow() && _startedProcess.WaitForExit(1000)))
                {
                    _startedProcess.Dispose();
                    _startedProcess = null;
                    return Task.FromResult(true);
                }
            }
            catch { }

            // Keep the handle to allow a later retry; prevent duplicate starts.
            return Task.FromResult(false);
        }
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
    internal static bool IsSafeHelperPath(string? path) =>
        GamingLocalFileSafetyPolicy.IsSafeRegularFile(path, GamingLocalFileSafetyPolicy.ExecutableExtensions);

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
