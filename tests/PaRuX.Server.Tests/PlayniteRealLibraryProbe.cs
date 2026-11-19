using PaRuX.Server.Orchestration.Gaming;
using Xunit;
using Xunit.Abstractions;

namespace PaRuX.Server.Tests;

/// <summary>Opt-in probe: set PARUX_PLAYNITE_LIBRARY to a COPY of a Playnite library folder to see what the importer would do. Skipped otherwise.</summary>
public sealed class PlayniteRealLibraryProbe(ITestOutputHelper output)
{
    [Fact]
    public void Describe_a_real_library_copy_when_provided()
    {
        string? folder = Environment.GetEnvironmentVariable("PARUX_PLAYNITE_LIBRARY");
        if (string.IsNullOrWhiteSpace(folder)) return;
        foreach (PlayniteImportGame g in PlayniteLibraryReader.Read(folder))
            output.WriteLine($"{(g.Importable ? "IMPORT" : "skip  ")} | {g.Kind,-11} | {g.Name} | {g.EmulatorId} | rom={g.GameFilePath.Length > 0} cover={g.CoverPath.Length > 0} fav={g.Favorite} sec={g.PlaySeconds} | {g.SkipReason}");
    }
}
