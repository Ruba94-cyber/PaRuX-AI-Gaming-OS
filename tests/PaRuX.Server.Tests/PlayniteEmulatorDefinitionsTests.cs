using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Definitions generated from Playnite's MIT emulator list; synthetic stand-in files only.</summary>
public sealed class PlayniteEmulatorDefinitionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-emu-gen-" + Guid.NewGuid().ToString("N"));
    public PlayniteEmulatorDefinitionsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void The_registry_now_covers_far_more_emulators_and_keeps_hand_tuned_entries_first()
    {
        Assert.True(EmulatorRegistry.Builtin.Count >= 100, "expected the hand-tuned list plus the generated definitions");
        Assert.Equal("eden", EmulatorRegistry.Builtin[0].Id);
        // A generated definition never replaces a hand-tuned one: Dolphin keeps its documented -b/-e template.
        Assert.Contains("-b", EmulatorRegistry.Find("dolphin")!.Template);
        Assert.Equal(EmulatorRegistry.Builtin.Count, EmulatorRegistry.Builtin.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(PlayniteEmulatorDefinitions.All, d => Assert.Equal("unverified", d.TemplateStatus));
        Assert.All(PlayniteEmulatorDefinitions.All, d => Assert.False(d.RequiresCore));
    }

    [Fact]
    public void A_path_embedded_in_an_argument_is_expanded_and_quoted_as_one_argument()
    {
        EmulatorDefinition? def = PlayniteEmulatorDefinitions.All.FirstOrDefault(d =>
            d.Template.Any(t => t != "{rom}" && t.Contains("{rom}", StringComparison.Ordinal)));
        if (def is null) return; // the current Playnite list has no such profile; the hand-built case below still covers the builder
        string rom = Path.Combine(_root, "My Games", "Demo Disc" + (def.GameExtensions.FirstOrDefault() ?? ".iso"));
        Directory.CreateDirectory(Path.GetDirectoryName(rom)!);
        File.WriteAllText(rom, "fixture");
        EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(def, rom, fullscreen: false, extraArguments: null, corePath: null);
        Assert.Null(plan.Error);
        Assert.DoesNotContain("{rom", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains("My Games", plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void An_embedded_placeholder_is_replaced_inside_a_larger_token_for_any_definition()
    {
        var def = new EmulatorDefinition("t", "T", "Test", new[] { "t.exe" }, new[] { ".iso" }, Array.Empty<string>(),
            new[] { "--exec={rom}", "-name={romname}", "--dir={romdir}" }, null, false);
        string rom = Path.Combine(_root, "a b", "Game One.iso");
        EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(def, rom, false, null, null);
        Assert.Null(plan.Error);
        Assert.Equal(new[] { "--exec=" + rom, "-name=Game One", "--dir=" + Path.GetDirectoryName(rom) }, plan.ArgumentList);
        Assert.StartsWith("\"--exec=", plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Disk_discovery_finds_a_generated_emulator_by_its_executable_name()
    {
        EmulatorDefinition sample = PlayniteEmulatorDefinitions.All.First();
        string exe = Path.Combine(_root, "tools", sample.ExecutableNames[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, new byte[20_000]);
        var detected = EmulatorDiscovery.Detect(new[] { _root });
        Assert.Contains(detected, d => string.Equals(d.EmulatorId, sample.Id, StringComparison.OrdinalIgnoreCase)
            || PlayniteEmulatorDefinitions.All.Any(x => x.ExecutableNames.Contains(sample.ExecutableNames[0]) && d.EmulatorId == x.Id));
    }
}
