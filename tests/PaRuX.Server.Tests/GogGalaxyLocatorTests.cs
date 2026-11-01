using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GogGalaxyLocatorTests
{
    [Fact]
    public void Only_local_existing_regular_galaxy_client_is_accepted()
    {
        string directory = Path.Combine(Path.GetTempPath(), "parux-galaxy-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string exe = Path.Combine(directory, "GalaxyClient.exe");
            File.WriteAllText(exe, "fixture; never execute");
            Assert.Equal(exe, GogGalaxyLocator.FindExecutable(new[] { exe }));
            Assert.Equal(exe, GogGalaxyLocator.FindExecutable(new[]
            {
                @"\\server\share\GalaxyClient.exe",
                Path.Combine(directory, "missing.exe"),
                exe
            }));
            Assert.Null(GogGalaxyLocator.FindExecutable(new[]
            {
                @"\\server\share\GalaxyClient.exe",
                Path.Combine(directory, "GalaxyClient.cmd"),
                Path.Combine(directory, "missing.exe"),
                "relative\\GalaxyClient.exe"
            }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("1207659123", true)]
    [InlineData("123456789012345678901234", true)]
    [InlineData("1", true)]
    [InlineData("", false)]
    [InlineData("a123", false)]
    [InlineData("12 /command=runGame", false)]
    [InlineData("1234567890123456789012345", false)]
    public void Galaxy_launch_accepts_only_decimal_installed_identifiers(string id, bool expected)
    {
        Assert.Equal(expected, ManualGameSessionService.ValidGogGameId(id));
    }
}
