using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SteamInstallLocatorTests
{
    [Fact]
    public void Selects_existing_regular_steam_executable_from_custom_local_install()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-steam-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string steam = Path.Combine(root, "steam.exe");
            string notSteam = Path.Combine(root, "other.exe");
            File.WriteAllText(steam, "Fixture only. Never execute.");
            File.WriteAllText(notSteam, "Fixture only.");
            string? discovered = SteamInstallLocator.FindExecutable(
                new[] { notSteam, Path.Combine(root, "missing", "steam.exe"), steam });

            Assert.Equal(Path.GetFullPath(steam), discovered);
            Assert.Equal("Fixture only. Never execute.", File.ReadAllText(steam));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Ignores_relative_network_and_untrusted_executable_names()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-steam-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string wrong = Path.Combine(root, "steam-helper.exe");
            File.WriteAllText(wrong, "Fixture only");
            Assert.Null(SteamInstallLocator.FindExecutable(new[]
            {
                "steam.exe",
                @"\\server\games\steam.exe",
                wrong,
                Path.Combine(root, "missing", "steam.exe")
            }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Registry_candidates_cannot_become_command_line_arguments()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-steam-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string stem = Path.Combine(root, "steam.exe");
            File.WriteAllText(stem, "Fixture only");
            Assert.Null(SteamInstallLocator.FindExecutable(new[] { stem + " -applaunch 42" }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
