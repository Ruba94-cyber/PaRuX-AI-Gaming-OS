using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Per-game launch preferences layered over a library entry. Every field is optional; an absent profile
/// (or an old entry written before a field existed) means "launch exactly as the library entry says".
/// </summary>
public sealed class GameLaunchProfile
{
    public const int CurrentSchemaVersion = 1;
    /// <summary>Entry schema; entries without it (older files) are migrated to the current one on load.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Connected display to place the game window on (same id as the display topology list).</summary>
    public string? DisplayId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RefreshHz { get; set; }
    public bool? Fullscreen { get; set; }
    /// <summary>system (default) | prefer | require. Never changes OS HDR.</summary>
    public string HdrMode { get; set; } = "system";
    /// <summary>Honest statement for clients: fullscreen is stored as a preference. PaRuX never forces a window state on an
    /// arbitrary executable (the owned-window service refuses to resize games); emulator launches carry their own
    /// fullscreen switch. Read-only: ignored on input.</summary>
    public string FullscreenSupport => "preference_only";
    /// <summary>auto | xbox | playstation | switch | generic | keyboard_mouse</summary>
    public string ControllerPreset { get; set; } = "auto";
    /// <summary>balanced | performance | quality | quiet</summary>
    public string PerformanceProfile { get; set; } = "balanced";
    /// <summary>Extra arguments appended after the library arguments. Passed as ArgumentList, never through a shell.
    /// May contain {width}, {height} and {refresh}.</summary>
    public List<string> LaunchArgs { get; set; } = new();
    public GameStreamingSettings Streaming { get; set; } = new();
    /// <summary>Optional, opt-in one-shot actions targeting only a uniquely verified owned game window.</summary>
    public GamePostLaunchSettings PostLaunch { get; set; } = new();
    /// <summary>Only "hard_off": AI children stay off while the game runs. Any other value is rejected.</summary>
    public string AiConcurrency { get; set; } = GameLaunchProfileValidator.AiHardOff;
}

/// <summary>
/// Optional actions after an explicit direct executable game launch.
/// All flags default off: a legacy profile never activates, types or clicks on Windows.
/// This cannot guarantee exclusive fullscreen or override OS foreground restrictions.
/// </summary>
public sealed class GamePostLaunchSettings
{
    public bool BringToForeground { get; set; }
    public bool RequestAltEnterFullscreen { get; set; }
    public bool ClickClientCenter { get; set; }
    /// <summary>Delay after the verified process starts. Hard-capped by validation.</summary>
    public int DelayMs { get; set; } = 1200;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Requested => BringToForeground || RequestAltEnterFullscreen || ClickClientCenter;
}

public sealed class GameStreamingSettings
{
    public bool Enabled { get; set; }
    public int? BitrateKbps { get; set; }
    public int? Fps { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>auto | h264 | hevc | av1</summary>
    public string Codec { get; set; } = "auto";
}

public static class GameLaunchProfileValidator
{
    public const string AiHardOff = "hard_off";
    public const int MaxArgs = 32, MaxArgLength = 256;
    private static readonly string[] Controllers = { "auto", "xbox", "playstation", "switch", "generic", "keyboard_mouse" };
    private static readonly string[] Performance = { "balanced", "performance", "quality", "quiet" };
    private static readonly string[] Codecs = { "auto", "h264", "hevc", "av1" };
    private static readonly string[] HdrModes = { "system", "prefer", "require" };
    private static readonly string[] Placeholders = { "{width}", "{height}", "{refresh}" };
    /// <summary>Rejected even though ArgumentList never reaches a shell: they only make sense as injection.</summary>
    private static readonly char[] Forbidden = "&|;<>^`$%\"".ToCharArray();

    /// <returns>null when valid, otherwise a user-facing reason (nothing is stored).</returns>
    public static string? Validate(GameLaunchProfile? p)
    {
        if (p is null) return "Profile is required.";
        if (p.SchemaVersion is < 0 or > GameLaunchProfile.CurrentSchemaVersion) return "Unsupported profile version.";
        if (p.DisplayId is { } d && (d.Length is 0 or > 300 || d.Any(char.IsControl))) return "Invalid display id.";
        if ((p.Width is null) != (p.Height is null)) return "Width and height must be set together.";
        if (p.Width is { } w && (w < 320 || w > 15360)) return "Width must be 320-15360.";
        if (p.Height is { } h && (h < 240 || h > 8640)) return "Height must be 240-8640.";
        if (p.RefreshHz is { } r && (r < 24 || r > 1000)) return "Refresh rate must be 24-1000 Hz.";
        if (!In(Controllers, p.ControllerPreset)) return "Unknown controller preset.";
        if (!In(Performance, p.PerformanceProfile)) return "Unknown performance profile.";
        if (!In(HdrModes, p.HdrMode)) return "HDR mode must be system, prefer or require.";
        if (!string.Equals(p.AiConcurrency, AiHardOff, StringComparison.Ordinal))
            return "AI concurrency must be 'hard_off': AI stays off while a game runs.";
        if (p.LaunchArgs is null || p.LaunchArgs.Count > MaxArgs) return "At most 32 launch arguments.";
        foreach (string a in p.LaunchArgs)
        {
            string? problem = ValidateArg(a);
            if (problem is not null) return problem;
            if ((a.Contains("{width}", StringComparison.Ordinal) && p.Width is null) ||
                (a.Contains("{height}", StringComparison.Ordinal) && p.Height is null) ||
                (a.Contains("{refresh}", StringComparison.Ordinal) && p.RefreshHz is null))
                return "A resolution placeholder is used but that value is not set.";
        }
        GameStreamingSettings s = p.Streaming ?? new();
        if (s.BitrateKbps is { } b && (b < 500 || b > 200_000)) return "Streaming bitrate must be 500-200000 kbps.";
        if (s.Fps is { } f && (f < 15 || f > 240)) return "Streaming fps must be 15-240.";
        if ((s.Width is null) != (s.Height is null)) return "Streaming width and height must be set together.";
        if (s.Width is { } sw && (sw < 320 || sw > 7680)) return "Streaming width must be 320-7680.";
        if (s.Height is { } sh && (sh < 240 || sh > 4320)) return "Streaming height must be 240-4320.";
        if (!In(Codecs, s.Codec)) return "Unknown streaming codec.";
        GamePostLaunchSettings a = p.PostLaunch ?? new();
        if (a.DelayMs is < 0 or > 8000) return "Post-launch delay must be 0-8000 ms.";
        if ((a.RequestAltEnterFullscreen || a.ClickClientCenter) && !a.BringToForeground)
            return "Fullscreen toggle and click require the opt-in foreground action.";
        return null;
    }

    private static bool In(string[] set, string? v) => v is not null && set.Contains(v, StringComparer.Ordinal);

    internal static string? ValidateArg(string? a)
    {
        if (string.IsNullOrWhiteSpace(a) || a.Length > MaxArgLength) return "Each launch argument must be 1-256 characters.";
        if (a.Any(char.IsControl)) return "Launch arguments cannot contain control characters.";
        if (a.IndexOfAny(Forbidden) >= 0) return "Launch arguments cannot contain shell metacharacters (& | ; < > ^ ` $ % \").";
        string rest = Placeholders.Aggregate(a, (acc, t) => acc.Replace(t, "", StringComparison.Ordinal));
        if (rest.IndexOfAny(new[] { '{', '}' }) >= 0) return "Unknown placeholder in launch argument.";
        foreach (string segment in a.Split('/', '\\', '='))
            if (segment.Trim() == "..") return "Launch arguments cannot traverse parent folders.";
        return null;
    }
}

/// <summary>Versioned per-game profile file, keyed by library id / title key. Atomic replace; a file that cannot be
/// read safely is never overwritten (writes are refused until it is repaired).</summary>
public sealed class GameLaunchProfileStore
{
    public const int FileVersion = 1;
    public const int MaxProfiles = 10_000;
    private sealed record FileModel(int Version, Dictionary<string, GameLaunchProfile> Profiles);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, GameLaunchProfile> _profiles = new(StringComparer.Ordinal);
    private string? _loadError;

    public GameLaunchProfileStore() : this(Path.Combine(PaRuXDataRoot.Path, "server", "game-launch-profiles.json")) { }
    public GameLaunchProfileStore(string path) { _path = Path.GetFullPath(path); Load(); }

    public string? LoadError { get { lock (_gate) return _loadError; } }

    public GameLaunchProfile? Get(string key)
    {
        lock (_gate)
        {
            Load(); // pick up another Core's edits
            return _profiles.TryGetValue(key, out var p) ? Clone(p) : null;
        }
    }

    /// <returns>null on success, else a reason; nothing is changed on failure.</returns>
    public string? Save(string key, GameLaunchProfile profile)
    {
        if (!GameTitleEndpoints.ValidTitleKey(key)) return "Invalid game key.";
        Normalize(profile);
        string? problem = GameLaunchProfileValidator.Validate(profile);
        if (problem is not null) return problem;
        lock (_gate)
        {
            Load();
            if (_loadError is not null) return _loadError;
            if (!_profiles.ContainsKey(key) && _profiles.Count >= MaxProfiles) return "Profile limit reached.";
            profile.SchemaVersion = GameLaunchProfile.CurrentSchemaVersion;
            var next = new Dictionary<string, GameLaunchProfile>(_profiles, StringComparer.Ordinal) { [key] = Clone(profile) };
            if (!Persist(next)) return "The profile could not be saved.";
            _profiles = next;
            return null;
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            Load();
            if (_loadError is not null || !_profiles.ContainsKey(key)) return false;
            var next = new Dictionary<string, GameLaunchProfile>(_profiles, StringComparer.Ordinal);
            next.Remove(key);
            if (!Persist(next)) return false;
            _profiles = next;
            return true;
        }
    }

    private static void Normalize(GameLaunchProfile p)
    {
        p.LaunchArgs ??= new();
        p.Streaming ??= new();
        p.PostLaunch ??= new();
        p.ControllerPreset ??= "auto";
        p.PerformanceProfile ??= "balanced";
        p.HdrMode ??= "system";
        p.AiConcurrency ??= GameLaunchProfileValidator.AiHardOff;
        p.Streaming.Codec ??= "auto";
    }

    private void Load()
    {
        lock (_gate)
        {
            _loadError = null;
            _profiles = new(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(_path)) return;
                if (new FileInfo(_path).Length > 16 * 1024 * 1024) { _loadError = "The launch profile file is too large."; return; }
                FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
                if (model is null || model.Version < 1 || model.Version > FileVersion || model.Profiles is null ||
                    model.Profiles.Count > MaxProfiles)
                { _loadError = "The launch profile file has an unsupported format."; return; }
                foreach (var (key, profile) in model.Profiles)
                {
                    if (profile is null || !GameTitleEndpoints.ValidTitleKey(key))
                    { _loadError = "The launch profile file has invalid entries."; return; }
                    Normalize(profile); // migration: missing fields take defaults
                    if (GameLaunchProfileValidator.Validate(profile) is not null)
                    { _loadError = "The launch profile file has invalid entries."; return; }
                    profile.SchemaVersion = GameLaunchProfile.CurrentSchemaVersion;
                }
                _profiles = new(model.Profiles, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { _loadError = "The launch profile file could not be read."; }
        }
    }

    private bool Persist(Dictionary<string, GameLaunchProfile> profiles)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(f, new FileModel(FileVersion, profiles), JsonOptions);
                f.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { if (temp is not null) try { File.Delete(temp); } catch (Exception) { } }
    }

    private static GameLaunchProfile Clone(GameLaunchProfile p) =>
        JsonSerializer.Deserialize<GameLaunchProfile>(JsonSerializer.Serialize(p, JsonOptions), JsonOptions)!;
}

/// <summary>Seam so tests can observe what would be started without spawning a process.</summary>
public interface IGameProcessStarter
{
    /// <returns>true when the OS reported the process started.</returns>
    Task<bool> StartAsync(ProcessStartInfo info, CancellationToken ct);
}

public sealed record GameLaunchApplyResult(bool Started, string Message, string? DisplayId);

public static class GameLaunchProfileApplier
{
    /// <summary>Library arguments (split Windows-style) + profile arguments, as ArgumentList. UseShellExecute is false.</summary>
    public static ProcessStartInfo BuildStartInfo(ManualGameDto game, GameLaunchProfile? profile)
    {
        string path = Path.GetFullPath(game.ExecutablePath);
        var info = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = string.IsNullOrEmpty(game.WorkingDirectory) ? Path.GetDirectoryName(path)! : game.WorkingDirectory,
            UseShellExecute = false
        };
        if (profile is null || profile.LaunchArgs.Count == 0)
        {
            info.Arguments = game.Arguments ?? string.Empty; // unchanged legacy behaviour
            return info;
        }
        foreach (string a in SplitCommandLine(game.Arguments ?? string.Empty)) info.ArgumentList.Add(a);
        CultureInfo inv = CultureInfo.InvariantCulture;
        foreach (string a in profile.LaunchArgs)
            info.ArgumentList.Add(a
                .Replace("{width}", (profile.Width ?? 0).ToString(inv), StringComparison.Ordinal)
                .Replace("{height}", (profile.Height ?? 0).ToString(inv), StringComparison.Ordinal)
                .Replace("{refresh}", (profile.RefreshHz ?? 0).ToString(inv), StringComparison.Ordinal));
        return info;
    }

    /// <summary>Re-validates and starts through the seam. Cancellation before start launches nothing.</summary>
    public static async Task<GameLaunchApplyResult> ApplyAsync(ManualGameDto game, GameLaunchProfile? profile,
        IGameProcessStarter starter, CancellationToken ct)
    {
        if (profile is not null && GameLaunchProfileValidator.Validate(profile) is { } problem)
            return new(false, "The saved launch profile is invalid: " + problem, null);
        ct.ThrowIfCancellationRequested();
        bool ok = await starter.StartAsync(BuildStartInfo(game, profile), ct).ConfigureAwait(false);
        return new(ok, ok ? "Started." : "Windows did not start the requested executable.", profile?.DisplayId);
    }

    /// <summary>Command-line text for the UAC "runas" path, which cannot use ArgumentList.</summary>
    public static string ToCommandLine(ProcessStartInfo info)
    {
        if (info.ArgumentList.Count == 0) return info.Arguments ?? string.Empty;
        var sb = new StringBuilder();
        foreach (string a in info.ArgumentList)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t' }) < 0) { sb.Append(a); continue; }
            sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
        }
        return sb.ToString();
    }

    internal static List<string> SplitCommandLine(string text)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false, has = false;
        foreach (char c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; has = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes) { if (has) { result.Add(cur.ToString()); cur.Clear(); has = false; } }
            else { cur.Append(c); has = true; }
        }
        if (has) result.Add(cur.ToString());
        return result;
    }
}
