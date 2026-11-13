using System.Text.Json;

namespace PaRuX.Server;

/// <summary>What the text runtime was serving when Gaming Mode hard-off stopped it.</summary>
internal sealed record GamingUnloadedText(
    string ModelPath, string? Role, string? ProfileLabel, string TextProfile, DateTime UnloadedUtc);

/// <summary>
/// PERF-003c. Gaming hard-off stops the text runtime, so the model that was loaded is gone when the game ends. The
/// product rule (same as speech and retrieval) is lazy recovery: nothing heavy is loaded automatically when Gaming ends;
/// the next request loads again. This remembers WHAT was loaded so that next request (and the status endpoints) can say
/// and do the right thing instead of silently falling back to the default profile. State lives in the Core state
/// directory, so a Core restart during a game does not forget it. Pure file I/O, no process control.
/// </summary>
internal static class GamingTextMemory
{
    private static readonly object Gate = new();

    internal static void Save(GamingUnloadedText state)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalAiPaths.GamingUnloadedFile)!);
                File.WriteAllText(LocalAiPaths.GamingUnloadedFile, JsonSerializer.Serialize(state));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The remembered state, or null when nothing is remembered or the file is unreadable (never a guess).</summary>
    internal static GamingUnloadedText? Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(LocalAiPaths.GamingUnloadedFile)) return null;
                GamingUnloadedText? state = JsonSerializer.Deserialize<GamingUnloadedText>(File.ReadAllText(LocalAiPaths.GamingUnloadedFile));
                return state is null || string.IsNullOrWhiteSpace(state.ModelPath) ? null : state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            try { File.Delete(LocalAiPaths.GamingUnloadedFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Pure decisions about restoring the text model when Gaming ends.</summary>
internal static class GamingRestorePolicy
{
    /// <summary>
    /// Whether leaving Gaming should start loading the remembered model right away. Only the explicit always-on Local AI
    /// mode (<c>ON</c>, the pre-existing setting that keeps a model resident) asks for that; the default <c>AUTO</c>
    /// loads lazily on the next request and <c>OFF</c> never loads. A game that is active never allows a load.
    /// </summary>
    internal static bool ShouldEagerRestore(string mode, bool gamingActive, bool hasRememberedModel) =>
        hasRememberedModel && !gamingActive && string.Equals(mode, "ON", StringComparison.OrdinalIgnoreCase);

    /// <summary>Honest one-line status for a text runtime that Gaming unloaded and nothing has reloaded yet.</summary>
    internal static string Describe(GamingUnloadedText remembered, bool gamingActive, string mode)
    {
        string model = SafeFileName(remembered.ModelPath);
        if (gamingActive) return "unloaded for Gaming (" + model + "); will reload on demand after Gaming ends";
        return string.Equals(mode, "OFF", StringComparison.OrdinalIgnoreCase)
            ? "unloaded for Gaming (" + model + "); Local AI is OFF, so it will not reload"
            : "unloaded for Gaming (" + model + "); will reload on demand";
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path) is { Length: > 0 } name ? name : path; }
        catch (ArgumentException) { return path; }
    }
}
