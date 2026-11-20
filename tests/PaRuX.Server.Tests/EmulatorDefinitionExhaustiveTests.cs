using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Every built-in emulator definition is exercised against synthetic files: a fake executable must be detected and a fake game
/// file with the definition's own extension must be found by the scan. This proves the definitions are internally consistent; it
/// does NOT prove that a real emulator accepts the launch arguments (the templates stay marked "unverified").
/// </summary>
public sealed class EmulatorDefinitionExhaustiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-emu-all-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Every_definition_has_an_id_a_name_an_executable_and_a_unique_id()
    {
        var all = EmulatorRegistry.Builtin;
        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (EmulatorDefinition d in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Id), "id");
            Assert.False(string.IsNullOrWhiteSpace(d.Name), d.Id + " name");
            Assert.NotEmpty(d.ExecutableNames);
            Assert.All(d.ExecutableNames, n => Assert.EndsWith(".exe", n, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(d.Template);
        }
    }

    [Fact]
    public void A_fake_executable_for_every_definition_is_detected_and_nothing_is_started()
    {
        var all = EmulatorRegistry.Builtin;
        foreach (EmulatorDefinition d in all)
        {
            string dir = Path.Combine(_root, "install", d.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, d.ExecutableNames[0]), new byte[24_000]);   // not a runnable file on purpose
        }

        var detected = EmulatorDiscovery.Detect(new[] { Path.Combine(_root, "install") }, maxEntries: EmulatorDiscovery.MaxDetectEntries);
        var found = detected.Select(x => x.EmulatorId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] missing = all.Where(d => !found.Contains(d.Id)).Select(d => d.Id).ToArray();
        Assert.True(missing.Length == 0, "Not detected from a fake executable: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_fake_game_file_with_each_definitions_own_extension_is_found_by_the_scan()
    {
        var problems = new List<string>();
        foreach (EmulatorDefinition d in EmulatorRegistry.Builtin)
        {
            string name = d.GameExtensions.Count > 0 ? "Sample Game" + d.GameExtensions[0] : d.GameFileNames.Count > 0 ? d.GameFileNames[0] : string.Empty;
            if (name.Length == 0) continue;   // definitions that start a program without a game file
            string folder = Path.Combine(_root, "roms", d.Id);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), new byte[4096]);
            EmulatorScanResult result = EmulatorDiscovery.Scan(d, folder);
            if (result.Error is not null || result.Games.Count == 0)
                problems.Add(d.Id + " (" + name + "): " + (result.Error ?? "no game found"));
        }
        Assert.True(problems.Count == 0, "Scan did not find its own fake game file for: " + string.Join("; ", problems));
    }

    [Fact]
    public void Every_definition_builds_a_launch_plan_for_a_fake_game_without_starting_anything()
    {
        var problems = new List<string>();
        foreach (EmulatorDefinition d in EmulatorRegistry.Builtin)
        {
            string exe = Path.Combine(_root, "plan", d.Id, d.ExecutableNames[0]);
            string rom = Path.Combine(_root, "plan", d.Id, "Sample Game" + (d.GameExtensions.Count > 0 ? d.GameExtensions[0] : ".bin"));
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, new byte[24_000]);
            File.WriteAllBytes(rom, new byte[4096]);
            try
            {
                var tokens = d.Template.Select(t => t.Replace("{rom}", rom).Replace("{romdir}", Path.GetDirectoryName(rom)!).Replace("{romname}", "Sample Game")).ToList();
                // These are filled by EmulatorCommandBuilder from the owner's own setup (extra arguments, launch mode, input profile, core).
                string[] builderTokens = { "{extra}", "{fullscreen}", "{input-profile}", "{controller-ui}", "{core}" };
                string[] unknown = tokens.SelectMany(t => System.Text.RegularExpressions.Regex.Matches(t, @"\{[^{}]*\}").Select(m => m.Value))
                    .Where(v => !builderTokens.Contains(v)).ToArray();
                if (unknown.Length > 0) problems.Add(d.Id + ": unknown placeholder " + string.Join(", ", unknown));
            }
            catch (Exception ex) { problems.Add(d.Id + ": " + ex.Message); }
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
