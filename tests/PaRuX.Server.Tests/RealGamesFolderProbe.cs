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

    [Fact]
    public void A_real_rom_folder_is_scanned_with_the_matching_emulator_definition()
    {
        string? roms = Environment.GetEnvironmentVariable("PARUX_REAL_ROMS_DIR");
        string? emulatorId = Environment.GetEnvironmentVariable("PARUX_REAL_ROMS_EMULATOR");
        if (string.IsNullOrWhiteSpace(roms) || string.IsNullOrWhiteSpace(emulatorId) || !Directory.Exists(roms)) return;
        var definition = PaRuX.Server.Orchestration.Gaming.Emulators.EmulatorRegistry.Builtin.First(d => string.Equals(d.Id, emulatorId, StringComparison.OrdinalIgnoreCase));
        var result = PaRuX.Server.Orchestration.Gaming.Emulators.EmulatorDiscovery.Scan(definition, roms);
        foreach (var g in result.Games) output.WriteLine("rom: " + g.GetType().GetProperty("Name")?.GetValue(g) + " | " + g.GetType().GetProperty("FilePath")?.GetValue(g));
        output.WriteLine("count=" + result.Games.Count + " error=" + result.Error);
        Assert.Null(result.Error);
        Assert.NotEmpty(result.Games);
    }

    [Fact]
    public async Task A_real_installer_is_checksummed_without_being_run()
    {
        string? file = Environment.GetEnvironmentVariable("PARUX_REAL_INSTALLER");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
        var service = new GameInstallerInspectionService(() => false);
        var result = await service.InspectAsync(new PaRuX.Contracts.GameInstallerInspectionRequest(file), CancellationToken.None);
        output.WriteLine($"installer: {result.FileName} | {result.SizeBytes} bytes | sha256={result.Sha256[..16]}... | advisory={result.Advisory}");
        Assert.Equal(64, result.Sha256.Length);
        Assert.Equal(new FileInfo(file).Length, result.SizeBytes);
        Assert.Contains("did not check malware", result.Advisory, StringComparison.OrdinalIgnoreCase);
    }
}
