using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SwitchTitleFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-switch-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("Mario Kart 8 Deluxe[0100152000022000][US][v0].nsp", false)]            // base title: id ends in 000
    [InlineData("Mario Kart 8 Deluxe 3.0.1 [0100152000022800][v1179648][UPD].nsp", true)] // update: tag and id ending 800
    [InlineData("Mario Kart 8 Deluxe [Booster Course][0100152000023001][v65536][US].nsp", true)]   // add-on content: any other id
    [InlineData("Pokémon Legends Z-A [0100F43008C44800][DLC][US].nsp", true)]             // explicit DLC tag wins
    [InlineData("Some Game [Update v1.0.1].nsp", true)]
    [InlineData("Game Without An Id.nsp", false)]
    [InlineData("Game [0100152000022800].iso", false)]                                   // not a Switch container: never touched
    [InlineData("Game [0100152000022000].XCI", false)]
    [InlineData("", false)]
    public void Updates_and_add_ons_are_told_apart_from_games(string fileName, bool addOn) =>
        Assert.Equal(addOn, SwitchTitleFiles.IsAddOn(fileName));

    [Fact]
    public void A_scan_lists_the_games_and_counts_the_updates_it_left_out()
    {
        EmulatorDefinition eden = EmulatorRegistry.Builtin.First(d => d.Id == "eden");
        Directory.CreateDirectory(Path.Combine(_root, "update"));
        foreach (string name in new[]
        {
            "Alpha Game[0100AAAA00000000][v0].nsp", "Beta Game [0100BBBB00000000][v0].nsp",
            "update/Alpha Game [0100AAAA00000800][v65536][UPD].nsp", "update/Alpha Game DLC [0100AAAA00001001].nsp",
        })
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[2048]);
        }

        EmulatorScanResult result = EmulatorDiscovery.Scan(eden, _root);

        Assert.Equal(2, result.Games.Count);
        Assert.Equal(2, result.SkippedAddOns);
        Assert.DoesNotContain(result.Games, g => g.Title.Contains("DLC", StringComparison.OrdinalIgnoreCase));
    }
}
