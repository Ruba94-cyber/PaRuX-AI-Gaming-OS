using System.Text.Json;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Durable description of the Gaming session PaRuX started, kept beside the game marker so a Core
/// crash/restart can resume the right session type (remote vs local vs manual game) instead of
/// adopting a profile-less "GamingLocal". It holds identifiers only: no paths, secrets or arguments.
/// </summary>
internal sealed record GamingSessionRecord(
    string? ProfileId, string? TriggerId, string? InputId, string? LauncherId, DateTime StartedUtc)
{
    private static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(LocalAiPaths.GameMarkerFile)!, "gaming-session.json");

    public static void Save(GamingSessionRecord record, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recovery data is best effort: failing to persist it must never fail a game launch.
            System.Diagnostics.Trace.TraceWarning("Gaming session record not saved: " + ex.Message);
        }
    }

    public static GamingSessionRecord? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return null;
            return JsonSerializer.Deserialize<GamingSessionRecord>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Delete(string? path = null)
    {
        try { File.Delete(path ?? DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
