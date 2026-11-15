using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-only gates for release signing coverage, shipped-binary metadata and the split-asset tooling.
/// They do not replace a real signed build; the packaging run verifies signatures with signtool.
/// </summary>
public sealed class ReleaseSigningAndSplitContractTests
{
    [Fact]
    public void Msi_and_portable_builds_sign_every_first_party_pe_through_the_shared_script()
    {
        string signer = ReadRepo("scripts", "sign-first-party.ps1");
        Assert.Contains("PaRuX*", signer, StringComparison.Ordinal);
        Assert.Contains("parux.exe", signer, StringComparison.Ordinal);
        Assert.Contains("verify /pa", signer, StringComparison.Ordinal);

        string msi = ReadRepo("scripts", "build-msi.ps1");
        Assert.Contains("sign-first-party.ps1", msi, StringComparison.Ordinal);
        Assert.Contains("-Root $publishDir", msi, StringComparison.Ordinal);
        // The Windows ML worker must be published (and thus signed) before WiX harvests it.
        Assert.Contains("$winMlWorkerProject", msi, StringComparison.Ordinal);
        Assert.Contains("PaRuXWinMlPrepublished", msi, StringComparison.Ordinal);
        Assert.Contains("PaRuXWinMlPrepublished", ReadRepo("installer", "PaRuX.Setup", "PaRuX.Setup.wixproj"), StringComparison.Ordinal);

        string portable = ReadRepo("scripts", "build-portable.ps1");
        Assert.Contains("sign-first-party.ps1", portable, StringComparison.Ordinal);
        Assert.True(
            portable.IndexOf("sign-first-party.ps1", StringComparison.Ordinal) <
            portable.IndexOf("$fileManifest = @(", StringComparison.Ordinal),
            "Portable files must be signed before the per-file inventory is hashed.");
    }

    [Theory]
    [InlineData("PaRuX.UpdateHelper")]
    [InlineData("PaRuX.WinMlWorker")]
    public void Helper_executables_carry_company_and_product_metadata(string project)
    {
        string csproj = ReadRepo("src", project, project + ".csproj");
        Assert.Contains("<Company>RBG</Company>", csproj, StringComparison.Ordinal);
        Assert.Contains("<Product>RBG – PaRuX AI &amp; Gaming OS</Product>", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_and_join_tools_enforce_the_github_asset_limit_and_verify_hashes()
    {
        string split = ReadRepo("tools", "split-release-asset.ps1");
        Assert.Contains("2040109465", split, StringComparison.Ordinal); // 1.9 GiB
        Assert.Contains(".part{1:D3}", split, StringComparison.Ordinal);
        Assert.Contains(".parts.sha256.txt", split, StringComparison.Ordinal);

        string join = ReadRepo("tools", "join-release-asset.ps1");
        Assert.Contains("SHA-256 mismatch", join, StringComparison.Ordinal);
        Assert.Contains("does not equal the expected", join, StringComparison.Ordinal);

        string verify = ReadRepo("scripts", "verify-release-assets.ps1");
        Assert.Contains("*.parts.sha256.txt", verify, StringComparison.Ordinal);
        Assert.Contains("do not hash to the listed joined SHA-256", verify, StringComparison.Ordinal);
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
