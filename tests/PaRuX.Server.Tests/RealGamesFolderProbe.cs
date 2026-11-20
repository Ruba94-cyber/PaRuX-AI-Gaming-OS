using PaRuX.Server.Orchestration.Gaming;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>
/// Opt-in probe over the REAL game folders on this PC: set PARUX_REAL_GAMES_DIR (for example C:\Games). For each game folder the
/// inspector must propose a title and at least one executable candidate, finish within its bounds and report no error. It only
/// reads directory entries: nothing is started, opened or hashed. Skipped when the variable is not set.
/// </summary>
public sealed class RealGamesFolderProbe(ITestOutputHelper output)
{
    [Fact]
    public void Every_real_game_folder_is_understood_without_starting_anything()
    {
        string? root = Environment.GetEnvironmentVariable("PARUX_REAL_GAMES_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        var problems = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            GameFolderInspection result = GameFolderInspector.Inspect(folder);
            watch.Stop();
            output.WriteLine($"{Path.GetFileName(folder)} => title='{result.SuggestedTitle}', {result.Candidates.Count} candidate(s), "
                + $"top={(result.Candidates.Count > 0 ? result.Candidates[0].RelativePath + " [" + result.Candidates[0].Reason + "]" : "none")}, "
                + $"truncated={result.Truncated}, {watch.ElapsedMilliseconds} ms, error={result.Error ?? "none"}");
            if (result.Error is not null) problems.Add(Path.GetFileName(folder) + ": " + result.Error);
            else if (string.IsNullOrWhiteSpace(result.SuggestedTitle)) problems.Add(Path.GetFileName(folder) + ": no suggested title");
            else if (result.Candidates.Count == 0) problems.Add(Path.GetFileName(folder) + ": no executable candidate");
            else if (watch.ElapsedMilliseconds > 15_000) problems.Add(Path.GetFileName(folder) + ": took " + watch.ElapsedMilliseconds + " ms");
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public async Task The_real_steam_library_is_discovered_when_a_steam_root_is_provided()
    {
        string? steam = Environment.GetEnvironmentVariable("PARUX_REAL_STEAM_ROOT");
        if (string.IsNullOrWhiteSpace(steam) || !Directory.Exists(steam)) return;
        var games = await new SteamGameDiscoveryProvider(steam).DiscoverAsync(CancellationToken.None);
        foreach (var g in games) output.WriteLine($"steam: {g.Name} | id={g.ProviderGameId} | state={g.InstallState}");
        // Only games registered with Steam (an appmanifest other than the 228980 redistributables) can be discovered.
        string apps = Path.Combine(steam, "steamapps");
        bool registered = Directory.Exists(apps) && Directory.EnumerateFiles(apps, "appmanifest_*.acf").Any(f => !f.EndsWith("appmanifest_228980.acf", StringComparison.OrdinalIgnoreCase));
        if (registered) Assert.NotEmpty(games); else Assert.Empty(games);
    }
}
