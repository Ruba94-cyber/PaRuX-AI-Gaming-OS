using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

public enum GameHookPhase { Pre, Post }

public sealed record GameHookRunResult(bool Ran, string Reason, int? ExitCode = null);

/// <summary>
/// Per-title pre/post-launch helper programs. OFF by default (a single global switch, persisted additively in
/// its own file; nothing is configured or run until the owner turns it on and picks a program). A hook is one
/// explicitly chosen local .exe admitted by <see cref="GamingLocalFileSafetyPolicy"/>: no shell, no scripts,
/// no elevation, no network paths or links, bounded arguments and a hard timeout (the process is killed on timeout).
/// Pre runs while AI is still ready (before Gaming is entered), Post only after AI was restored; a hook NEVER
/// runs while Gaming is active (the persisted Gaming marker) and is cancelled/killed if Gaming begins while it runs.
/// </summary>
public sealed class GameLaunchHookStore
{
    public const int MaxHooks = 2_000;
    private sealed record FileModel(int Version, bool Enabled, Dictionary<string, GameHooksDto> Hooks);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private bool _enabled;
    private Dictionary<string, GameHooksDto> _hooks = new(StringComparer.Ordinal);

    public GameLaunchHookStore() : this(Path.Combine(PaRuXDataRoot.Path, "server", "game-hooks.json")) { }

    public GameLaunchHookStore(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
        Load();
    }

    public GameHooksStateDto Snapshot()
    {
        lock (_gate) return new GameHooksStateDto(_enabled, new Dictionary<string, GameHooksDto>(_hooks, StringComparer.Ordinal));
    }

    public bool Enabled { get { lock (_gate) return _enabled; } }

    public GameHooksDto? Get(string titleKey)
    {
        lock (_gate) return _hooks.TryGetValue(titleKey, out GameHooksDto? h) ? h : null;
    }

    public bool SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            bool previous = _enabled;
            _enabled = enabled;
            if (Persist()) return true;
            _enabled = previous;
            return false;
        }
    }

    /// <summary>Validated, persisted set. Returns an error text, or null on success. A null/empty dto clears the title's hooks.</summary>
    public string? Set(string titleKey, GameHooksDto? hooks)
    {
        if (!GameTitleEndpoints.ValidTitleKey(titleKey)) return "Invalid title.";
        if (hooks is not null)
        {
            foreach (GameHookSpec? spec in new[] { hooks.Pre, hooks.Post })
            {
                string? problem = Validate(spec);
                if (problem is not null) return problem;
            }
        }
        lock (_gate)
        {
            var previous = new Dictionary<string, GameHooksDto>(_hooks, StringComparer.Ordinal);
            if (hooks is null || (hooks.Pre is null && hooks.Post is null)) _hooks.Remove(titleKey);
            else
            {
                if (!_hooks.ContainsKey(titleKey) && _hooks.Count >= MaxHooks) return "Too many hooks.";
                _hooks[titleKey] = hooks;
            }
            if (Persist()) return null;
            _hooks = previous;
            return "The hook settings could not be saved.";
        }
    }

    /// <summary>Spec admission: a safe local .exe, bounded printable arguments, 1..60 s timeout. Null spec is valid (unset).</summary>
    public static string? Validate(GameHookSpec? spec)
    {
        if (spec is null) return null;
        if (!GamingLocalFileSafetyPolicy.IsSafeRegularFile(spec.Path, GamingLocalFileSafetyPolicy.ExecutableExtensions))
            return "A hook must be an existing local .exe (no network path, shortcut or link).";
        if (spec.Arguments is null || !ManualGameLibrary.ValidArguments(spec.Arguments))
            return "Hook arguments must be at most 1024 characters without control characters.";
        if (spec.TimeoutSeconds is < 1 or > 60) return "The hook timeout must be 1 to 60 seconds.";
        return null;
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path) || new FileInfo(_path).Length > 4 * 1024 * 1024) return;
                FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
                if (model is null || model.Version != 1 || model.Hooks is null || model.Hooks.Count > MaxHooks) return;
                _enabled = model.Enabled;
                _hooks = new Dictionary<string, GameHooksDto>(
                    model.Hooks.Where(p => GameTitleEndpoints.ValidTitleKey(p.Key) && p.Value is not null), StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _enabled = false; // an unreadable file never turns hooks on
                _hooks = new Dictionary<string, GameHooksDto>(StringComparer.Ordinal);
            }
        }
    }

    private bool Persist()
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel(1, _enabled, _hooks), JsonOptions));
            File.Move(temp, _path, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public sealed class GameLaunchHookService
{
    private readonly GameLaunchHookStore _store;
    private readonly Func<bool> _gamingActive;
    private readonly ILogger<GameLaunchHookService>? _logger;

    public GameLaunchHookService(GameLaunchHookStore store, ILogger<GameLaunchHookService>? logger = null)
        : this(store, GamingHardOffWatch.MarkerActive, logger) { }

    internal GameLaunchHookService(GameLaunchHookStore store, Func<bool> gamingActive, ILogger<GameLaunchHookService>? logger = null)
    {
        _store = store;
        _gamingActive = gamingActive;
        _logger = logger;
    }

    public async Task<GameHookRunResult> RunAsync(GameHookPhase phase, string titleKey, CancellationToken ct)
    {
        if (!_store.Enabled) return new(false, "disabled");
        GameHooksDto? hooks = _store.Get(titleKey);
        GameHookSpec? spec = phase == GameHookPhase.Pre ? hooks?.Pre : hooks?.Post;
        if (spec is null) return new(false, "no-hook");
        if (_gamingActive()) return new(false, "gaming-active");
        // Revalidate at run time: the file may have been replaced by a link or removed since it was configured.
        if (GameLaunchHookStore.Validate(spec) is not null) return new(false, "unsafe-path");

        using var watch = new GamingHardOffWatch(_gamingActive, ct);
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = System.IO.Path.GetFullPath(spec.Path),
                WorkingDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(spec.Path))!,
                Arguments = spec.Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process = Process.Start(info);
            if (process is null) return new(false, "failed");
            try
            {
                await process.WaitForExitAsync(watch.Token).WaitAsync(TimeSpan.FromSeconds(spec.TimeoutSeconds), CancellationToken.None)
                    .ConfigureAwait(false);
                return new(true, "completed", process.ExitCode);
            }
            catch (TimeoutException)
            {
                KillOwned(process);
                return new(true, "timeout");
            }
            catch (OperationCanceledException)
            {
                KillOwned(process);
                return new(true, watch.Triggered ? "gaming-started" : "cancelled");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            _logger?.LogInformation("A launch hook could not be started: {Type}", ex.GetType().Name);
            return new(false, "failed");
        }
        finally { process?.Dispose(); }
    }

    /// <summary>Only the process this service started (and its children) is ended, never anything else.</summary>
    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
