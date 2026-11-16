namespace PaRuX.Contracts;

/// <summary>Client view of the server's per-game launch profile (same JSON shape).</summary>
public sealed class GameLaunchProfileDto
{
    public int SchemaVersion { get; set; } = 1;
    public string? DisplayId { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RefreshHz { get; set; }
    public bool? Fullscreen { get; set; }
    /// <summary>system | prefer | require. Readiness check only; no OS HDR toggle.</summary>
    public string HdrMode { get; set; } = "system";
    public string ControllerPreset { get; set; } = "auto";
    public string PerformanceProfile { get; set; } = "balanced";
    public List<string> LaunchArgs { get; set; } = new();
    /// <summary>Opt-in owned game-window actions; absent in legacy profiles.</summary>
    public GamePostLaunchDto PostLaunch { get; set; } = new();
    /// <summary>Opaque pass-through so a save from the UI never drops streaming settings.</summary>
    public System.Text.Json.JsonElement? Streaming { get; set; }
    public string AiConcurrency { get; set; } = "hard_off";
    /// <summary>Server statement of what Fullscreen does ("preference_only").</summary>
    public string? FullscreenSupport { get; set; }
}

/// <summary>Per-game post-start actions only for a directly started and verified game window.</summary>
public sealed class GamePostLaunchDto
{
    public bool BringToForeground { get; set; }
    public bool RequestAltEnterFullscreen { get; set; }
    public bool ClickClientCenter { get; set; }
    public int DelayMs { get; set; } = 1200;
}

/// <summary>Pure form helpers for the launch-profile section (parsing only; the server owns validation).</summary>
public static class GameLaunchProfileForm
{
    public static readonly string[] ControllerPresets = { "auto", "xbox", "playstation", "switch", "generic", "keyboard_mouse" };
    public static readonly string[] PerformanceProfiles = { "balanced", "performance", "quality", "quiet" };

    /// <summary>Launch arguments are typed on one line (works with the on-screen keyboard) and split on whitespace.</summary>
    public static List<string> ParseArgs(string? text) =>
        (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    public static string JoinArgs(IEnumerable<string>? args) => string.Join(' ', args ?? Array.Empty<string>());

    /// <returns>true when empty (null) or a whole number; false when text is present but not a number.</returns>
    public static bool TryParseOptionalInt(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)) return false;
        value = n;
        return true;
    }
}
