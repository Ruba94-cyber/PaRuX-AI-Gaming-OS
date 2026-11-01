using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

public sealed record HidHideState(bool CloakOn, IReadOnlyList<string> RegisteredApps, IReadOnlyList<string> HiddenDevices);

public sealed record HidHideResult(bool Success, string Message);

/// <summary>
/// Optional integration with HidHide (https://github.com/nefarius/HidHide, MIT), the driver that hides a physical
/// controller from games so that only its virtual replacement is seen (no double input). PaRuX uses the vendor's own
/// command-line tool, never touches the driver directly, and never installs it.
///
/// Conservative by design, as the HidHide maintainers ask of third-party software: it only undoes exactly what it did.
/// Before it changes anything it writes a journal of the changes it is about to make (devices it hid, whether it
/// registered an application, whether it switched the cloak on); <see cref="RestoreAsync"/> and
/// <see cref="RecoverAsync"/> reverse precisely those and nothing else, so a user's own HidHide setup is left alone.
/// STATUS: SOURCE IMPLEMENTED, NOT RUN against a real HidHide installation (output formats come from its source).
/// </summary>
public sealed class HidHideManager
{
    internal readonly record struct ProcessSpec(string FileName, IReadOnlyList<string> Arguments);

    private sealed record Journal(List<string> HiddenByUs, string? AppRegisteredByUs, bool CloakTurnedOnByUs);

    private static readonly Regex DevicePathPattern = new(@"^[A-Za-z0-9_&\\#\-\{\}\.]{5,300}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex QuotedValue = new("\"([^\"]+)\"", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly Func<string?> _locateCli;
    private readonly Func<ProcessSpec, CancellationToken, Task<(int ExitCode, string Output)>> _run;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HidHideManager()
        : this(DefaultLocate, RunAsync, Path.Combine(Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!, "hidhide-journal.json")) { }

    internal HidHideManager(Func<string?> locateCli,
        Func<ProcessSpec, CancellationToken, Task<(int ExitCode, string Output)>> run, string journalPath)
    {
        _locateCli = locateCli;
        _run = run;
        _journalPath = journalPath;
    }

    public bool IsAvailable => _locateCli() is not null;
    /// <summary>Only reports the existence of a recovery transaction, not device paths.</summary>
    public bool RecoveryRequired => File.Exists(_journalPath);

    public async Task<HidHideState?> GetStateAsync(CancellationToken ct)
    {
        string? cli = _locateCli();
        if (cli is null) return null;
        var cloak = await _run(new(cli, new[] { "--cloak-state" }), ct).ConfigureAwait(false);
        var apps = await _run(new(cli, new[] { "--app-list" }), ct).ConfigureAwait(false);
        var devices = await _run(new(cli, new[] { "--dev-list" }), ct).ConfigureAwait(false);
        if (cloak.ExitCode != 0 || apps.ExitCode != 0 || devices.ExitCode != 0) return null;
        return new HidHideState(
            cloak.Output.Contains("--cloak-on", StringComparison.Ordinal),
            QuotedValues(apps.Output), QuotedValues(devices.Output));
    }

    /// <summary>Hides the chosen physical controllers from games and lets PaRuX itself still read them.</summary>
    public async Task<HidHideResult> HideAsync(IReadOnlyList<string> deviceInstancePaths, string paruxExecutable, CancellationToken ct)
    {
        if (deviceInstancePaths.Count is 0 or > 8) return new(false, "Choose between 1 and 8 controllers.");
        if (deviceInstancePaths.Any(p => !DevicePathPattern.IsMatch(p ?? string.Empty) || p!.StartsWith("--", StringComparison.Ordinal)))
            return new(false, "A controller identifier is not valid.");
        if (!Providers.CustomExecutableInputProvider.IsSafeHelperPath(paruxExecutable))
            return new(false, "The PaRuX program path is not valid.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? cli = _locateCli();
            if (cli is null) return new(false, "HidHide is not installed. It is optional; PaRuX never installs drivers on its own.");
            if (File.Exists(_journalPath)) return new(false, "A previous HidHide change has not been restored yet.");

            HidHideState? before = await GetStateAsync(ct).ConfigureAwait(false);
            if (before is null) return new(false, "HidHide did not answer. It may need administrator rights.");

            string fullApp = Path.GetFullPath(paruxExecutable);
            var journal = new Journal(
                deviceInstancePaths.Where(d => !before.HiddenDevices.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList(),
                before.RegisteredApps.Contains(fullApp, StringComparer.OrdinalIgnoreCase) ? null : fullApp,
                !before.CloakOn);
            SaveJournal(journal); // written first: a crash after this point can still be undone

            try
            {
                foreach (string command in PlanFor(journal))
                {
                    string[] args = command.Split('\u0001');
                    var result = await _run(new(cli, args), ct).ConfigureAwait(false);
                    if (result.ExitCode == 0) continue;

                    bool restored = await RestoreCoreAsync(cli, journal, CancellationToken.None).ConfigureAwait(false);
                    return restored
                        ? new(false, "HidHide refused a change; the PaRuX changes were undone.")
                        : new(false, "HidHide refused a change, and automatic rollback was incomplete. The recovery journal was kept.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A command might have changed the driver's configuration before cancellation was observed.
                // Restore with an independent token, keeping the journal if the cleanup fails.
                try { await RestoreCoreAsync(cli, journal, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { }
                throw;
            }
            return new(true, "The controllers are hidden from games. PaRuX restores them when it stops.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Undoes exactly what <see cref="HideAsync"/> did.</summary>
    public async Task<HidHideResult> RestoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_journalPath)) return new(true, "Nothing to restore.");
            Journal? journal = LoadJournal();
            if (journal is null)
                return new(false, "HidHide recovery journal is unreadable or invalid. No restore commands were run; inspect the journal and HidHide device list before proceeding.");
            string? cli = _locateCli();
            if (cli is null) return new(false, "HidHide is no longer available, so the earlier change could not be undone automatically.");
            return await RestoreCoreAsync(cli, journal, ct).ConfigureAwait(false)
                ? new(true, "HidHide was restored.")
                : new(false, "Some HidHide changes could not be undone. Open HidHide and check the device list.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Called at start-up: a journal left by a crash is applied in reverse.</summary>
    public Task<HidHideResult> RecoverAsync(CancellationToken ct) => RestoreAsync(ct);

    private static IEnumerable<string> PlanFor(Journal journal)
    {
        const char Sep = '\u0001'; // argument separator inside the plan strings (never appears in a validated value)
        if (journal.AppRegisteredByUs is { } app) yield return $"--app-reg{Sep}{app}";
        foreach (string device in journal.HiddenByUs) yield return $"--dev-hide{Sep}{device}";
        if (journal.CloakTurnedOnByUs) yield return "--cloak-on";
    }

    private async Task<bool> RestoreCoreAsync(string cli, Journal journal, CancellationToken ct)
    {
        bool ok = true;
        if (journal.CloakTurnedOnByUs) ok &= (await _run(new(cli, new[] { "--cloak-off" }), ct).ConfigureAwait(false)).ExitCode == 0;
        foreach (string device in journal.HiddenByUs)
            ok &= (await _run(new(cli, new[] { "--dev-unhide", device }), ct).ConfigureAwait(false)).ExitCode == 0;
        if (journal.AppRegisteredByUs is { } app)
            ok &= (await _run(new(cli, new[] { "--app-unreg", app }), ct).ConfigureAwait(false)).ExitCode == 0;
        if (ok) DeleteJournal();
        return ok;
    }

    private static List<string> QuotedValues(string output) =>
        output.Split('\n').Select(line => QuotedValue.Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();

    private void SaveJournal(Journal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        string temp = _journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(journal));
        File.Move(temp, _journalPath, overwrite: true);
    }

    private Journal? LoadJournal()
    {
        try
        {
            if (!File.Exists(_journalPath) || new FileInfo(_journalPath).Length > 64 * 1024) return null;
            Journal? journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_journalPath));
            // A tampered journal must not make PaRuX run the CLI with arbitrary values.
            if (journal is null || journal.HiddenByUs is null || journal.HiddenByUs.Count > 8) return null;
            if (journal.HiddenByUs.Any(p => !DevicePathPattern.IsMatch(p ?? string.Empty) || p!.StartsWith("--", StringComparison.Ordinal))) return null;
            if (journal.AppRegisteredByUs is { } app && !Path.IsPathFullyQualified(app)) return null;
            return journal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void DeleteJournal()
    {
        try { File.Delete(_journalPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? DefaultLocate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Nefarius Software Solutions e.U.\Nefarius Software Solutions e.U. HidHide");
            if (key?.GetValue("Path") is string installed && installed.Length is > 0 and < 500)
            {
                candidates.Add(Path.Combine(installed, "x64", "HidHideCLI.exe"));
                candidates.Add(Path.Combine(installed, "HidHideCLI.exe"));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe"));
        return candidates.FirstOrDefault(c => string.Equals(Path.GetFileName(c), "HidHideCLI.exe", StringComparison.OrdinalIgnoreCase)
                                              && Providers.CustomExecutableInputProvider.IsSafeHelperPath(c));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = spec.FileName, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in spec.Arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) { if (output.Length < 64 * 1024) output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) { if (output.Length < 64 * 1024) output.AppendLine(e.Data); } };
        try { process.Start(); }
        catch (Win32Exception) { return (-1, "could not start"); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            if (ct.IsCancellationRequested) throw;
            return (-2, "timed out");
        }
        process.WaitForExit();
        lock (output) return (process.ExitCode, output.ToString());
    }
}
