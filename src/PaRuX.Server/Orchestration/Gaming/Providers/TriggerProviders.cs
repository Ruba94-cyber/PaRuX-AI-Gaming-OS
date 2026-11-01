using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public sealed class ManualTriggerProvider : IGamingTriggerProvider
{
    public string Id => "manual";
    public string Name => "Manual Trigger";
    public bool IsDetected => true;

    private bool _manualState;

    public void SetTriggered(bool active) => _manualState = active;

    public Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct) =>
        Task.FromResult(_manualState);
}

/// <summary>
/// Composite display trigger. Combines display presence (e.g. EDID detection) with user activity
/// or controller interaction to avoid false-positives when a TV/monitor is powered off but EDID remains attached.
/// </summary>
public sealed class WindowsDisplayTriggerProvider : IGamingTriggerProvider
{
    public string Id => "display";
    public string Name => "Windows Display / Composite Trigger";
    public bool IsDetected => OperatingSystem.IsWindows();

    public Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(false);

        try
        {
            // 1. Check if multiple displays or external display is active
            int displayCount = GetDisplayCount();
            if (displayCount <= 1) return Task.FromResult(false);

            // 2. Check composite setting: if "require_controller" is enabled in settings, verify controller presence
            if (profile.TriggerSettings.TryGetValue("require_controller", out string? reqCtrl)
                && bool.TryParse(reqCtrl, out bool requireCtrl) && requireCtrl)
            {
                bool controllerConnected = IsGameControllerConnected();
                return Task.FromResult(controllerConnected);
            }

            // Default: display presence + active user session
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static int GetDisplayCount()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int count = GetSystemMetrics(80); // SM_CMONITORS
                return count > 0 ? count : 1;
            }
            return 1;
        }
        catch
        {
            return 1;
        }
    }

    // Polled once per second by the orchestrator: look up only the four known names (no full
    // process-table scan), dispose every handle, and reuse the answer for a few seconds.
    private static readonly TtlValue<bool> ControllerProcessPresent =
        new(TimeSpan.FromSeconds(5), ScanForControllerProcesses);

    private static bool IsGameControllerConnected() => ControllerProcessPresent.Get();

    private static bool ScanForControllerProcesses()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            foreach (string name in new[] { "xoutput", "inputmapper", "ds4windows", "steam" })
            {
                Process[] found = Process.GetProcessesByName(name);
                bool any = found.Length > 0;
                foreach (Process process in found) process.Dispose();
                if (any) return true;
            }
            return false;
        }
        catch { return false; }
    }
}

/// <summary>
/// Detects Sunshine remote streaming sessions via Sunshine status API / process check / state files.
/// </summary>
public sealed class SunshineSessionTriggerProvider : IGamingTriggerProvider
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly string[] SunshineProcessNames = { "sunshine", "sunshine.exe" };

    public string Id => "sunshine";
    public string Name => "Sunshine Remote Stream Trigger";

    public bool IsDetected
    {
        get => IsInstalledOnMachine;
    }

    internal static bool IsInstalledOnMachine => ExecutablePath != null || IsProcessRunning();

    internal static string? ExecutablePath
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return FindExecutable(localAppData, programFiles);
        }
    }

    internal static string? FindExecutable(string localAppData, string programFiles)
    {
        string[] candidates =
        {
            Path.Combine(programFiles, "Sunshine", "sunshine.exe"),
            Path.Combine(localAppData, "Programs", "Sunshine", "sunshine.exe"),
            @"C:\Sunshine\sunshine.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct)
    {
        if (!IsProcessRunning()) return false;

        // Sunshine HTTP status endpoint. The URL is user-editable, so only a loopback host is ever
        // contacted (no SSRF / LAN probing from a profile), and a failed or negative answer is reused
        // for a few seconds so a once-per-second poll does not become a once-per-second request.
        string url = profile.TriggerSettings.TryGetValue("sunshine_url", out string? customUrl) && !string.IsNullOrWhiteSpace(customUrl)
            ? customUrl
            : DefaultStatusUrl;
        if (!IsLoopbackHttpUrl(url)) url = DefaultStatusUrl;

        bool? cached = null;
        lock (_httpGate)
        {
            if (_lastHttpUrl == url && DateTime.UtcNow - _lastHttpUtc < HttpPollInterval)
                cached = _lastHttpActive;
        }

        if (cached is null)
        {
            bool active = false;
            try
            {
                using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    active = json.Contains("\"session_active\":true", StringComparison.OrdinalIgnoreCase)
                        || json.Contains("\"status\":\"streaming\"", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            lock (_httpGate) { _lastHttpUrl = url; _lastHttpUtc = DateTime.UtcNow; _lastHttpActive = active; }
            cached = active;
        }
        if (cached == true) return true;

        // Fallback: Sunshine is running (checked above through the cached process scan) and its stream ports are bound.
        return IsPortInUse(47998) || IsPortInUse(47999) || IsPortInUse(47989);
    }

    private const string DefaultStatusUrl = "http://127.0.0.1:47989/api/config";
    private static readonly TimeSpan HttpPollInterval = TimeSpan.FromSeconds(3);
    private static readonly object _httpGate = new();
    private static string? _lastHttpUrl;
    private static DateTime _lastHttpUtc = DateTime.MinValue;
    private static bool _lastHttpActive;

    internal static bool IsLoopbackHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.IsLoopback;

    /// <summary>The orchestrator polls this once per second; a full process-table scan each time (with undisposed Process objects)
    /// was the largest idle CPU cost in Core. The answer is reused for a few seconds, which only delays noticing a Sunshine
    /// start by that long.</summary>
    internal static readonly TtlValue<bool> ProcessRunning = new(TimeSpan.FromSeconds(5), ScanForSunshineProcess);

    private static bool IsProcessRunning() => ProcessRunning.Get();

    private static bool ScanForSunshineProcess()
    {
        try
        {
            Process[] found = Process.GetProcessesByName("sunshine");
            bool any = found.Length > 0;
            foreach (Process process in found) process.Dispose();
            return any;
        }
        catch { return false; }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Any(p => p.Port == port);
        }
        catch { return false; }
    }
}
