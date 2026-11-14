using System.Text.RegularExpressions;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-only drift gate: every shipped project, packaging script and the Android versionName
/// must derive from the single <c>PaRuXProductVersion</c> in Directory.Build.props.
/// A green assertion does not replace a real Windows publish or an installed-binary version check.
/// </summary>
public sealed class ReleaseVersionSourceContractTests
{
    private const string VersionProperty = "PaRuXProductVersion";

    private static readonly string[] ShippedProjects =
    {
        Path.Combine("src", "PaRuX.App", "PaRuX.App.csproj"),
        Path.Combine("src", "PaRuX.Server", "PaRuX.Server.csproj"),
        Path.Combine("src", "PaRuX.Cli", "PaRuX.Cli.csproj"),
        Path.Combine("src", "PaRuX.UpdateHelper", "PaRuX.UpdateHelper.csproj"),
        Path.Combine("src", "PaRuX.WinMlWorker", "PaRuX.WinMlWorker.csproj"),
    };

    [Fact]
    public void Central_product_version_is_defined_once_in_a_well_formed_props_file()
    {
        string props = ReadRepo("Directory.Build.props");
        Match match = Regex.Match(props, $@"<{VersionProperty}>(?<v>[^<]+)</{VersionProperty}>");
        Assert.True(match.Success, "Directory.Build.props must define " + VersionProperty + ".");
        Assert.Matches(@"^\d+\.\d+\.\d+$", match.Groups["v"].Value);
        Assert.Single(Regex.Matches(props, $@"<{VersionProperty}>"));
    }

    [Fact]
    public void Every_shipped_project_resolves_its_version_from_the_central_property()
    {
        foreach (string relative in ShippedProjects)
        {
            string csproj = ReadRepo(relative);
            Assert.Contains("<Version>$(" + VersionProperty + ")</Version>", csproj, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"<(Version|FileVersion|AssemblyVersion|InformationalVersion)>\s*\d", csproj);
        }
    }

    [Fact]
    public void Installer_wixproj_defaults_to_the_central_version_and_has_no_literal_default()
    {
        string wixproj = ReadRepo("installer", "PaRuX.Setup", "PaRuX.Setup.wixproj");
        Assert.Contains("$(" + VersionProperty + ")</ProductVersion>", wixproj, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"<ProductVersion[^>]*>\s*\d+\.\d+\.\d+\s*<", wixproj);
    }

    [Fact]
    public void No_packaging_script_carries_a_hard_coded_default_version_literal()
    {
        string scriptsDir = Path.Combine(RepoRoot(), "scripts");
        foreach (string script in Directory.GetFiles(scriptsDir, "*.ps1"))
        {
            string text = File.ReadAllText(script);
            Assert.False(
                Regex.IsMatch(text, @"\$(ProductVersion|Version)\s*=\s*""\d+\.\d+\.\d+"""),
                Path.GetFileName(script) + " must not default its version to a literal.");
        }
    }

    [Fact]
    public void Packaging_scripts_that_derive_the_version_read_the_central_props_file()
    {
        foreach (string script in new[]
                 {
                     "build-msi.ps1", "build-msix.ps1", "build-portable.ps1", "build-corporate-lite.ps1",
                     "build-winget-manifest.ps1", "publish-android-apk.ps1", "generate-sbom.ps1",
                     "build-full-offline-bundle.ps1",
                 })
        {
            string text = ReadRepo("scripts", script);
            Assert.Contains("Directory.Build.props", text, StringComparison.Ordinal);
            Assert.Contains(VersionProperty, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Packaging_scripts_never_pipe_a_LiteralPath_Include_listing_into_a_deletion()
    {
        // Windows PowerShell 5.1 ignores -Include when -LiteralPath is used, so "-LiteralPath X -Recurse -File -Include *.pdb
        // | Remove-Item" deletes every file under X (the MSIX layout was emptied by exactly this).
        var offenders = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "scripts"), "*.ps1"))
        {
            foreach (string line in File.ReadAllLines(file))
            {
                if (line.Contains("-LiteralPath", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("-Include", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("Remove-Item", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(Path.GetFileName(file) + ": " + line.Trim());
                }
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void Android_versionName_equals_the_central_product_version()
    {
        string version = ReadCentralVersion();
        string gradle = ReadRepo("src", "PaRuX.Android", "app", "build.gradle.kts");
        Match name = Regex.Match(gradle, @"versionName\s*=\s*""(?<v>[^""]+)""");
        Assert.True(name.Success, "Android build.gradle.kts must declare versionName.");
        Assert.Equal(version, name.Groups["v"].Value);
    }

    private static string ReadCentralVersion()
    {
        Match match = Regex.Match(
            ReadRepo("Directory.Build.props"), $@"<{VersionProperty}>(?<v>[^<]+)</{VersionProperty}>");
        Assert.True(match.Success);
        return match.Groups["v"].Value;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    private static string ReadRepo(params string[] relative)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
