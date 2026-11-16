using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-only packaging identity checks. A Windows package build and licence review
/// must still verify actual third-party model assets before redistribution.
/// </summary>
public sealed class CorporatePackModelIdentitySourceContractTests
{
    [Fact]
    public void User_provided_small_model_does_not_inherit_qwen4b_origin_or_licence_claim()
    {
        string script = Read("scripts", "build-corporate-lite.ps1");
        Assert.Contains("modelOriginLabel", script, StringComparison.Ordinal);
        Assert.Contains("modelLicenseLabel", script, StringComparison.Ordinal);
        Assert.Contains("modelSourceVerifiedByBuilder", script, StringComparison.Ordinal);
        Assert.Contains("user-supplied (origin not verified)", script, StringComparison.Ordinal);
        Assert.Contains("NOT VERIFIED - confirm licence before redistribution", script, StringComparison.Ordinal);
        Assert.Contains("The package checksum proves included bytes, not model origin", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Local Qwen3-4B GGUF chat model included", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Pinned_fetch_and_no_model_are_separate_and_custom_model_source_stays_explicit()
    {
        string script = Read("scripts", "build-corporate-lite.ps1");
        Assert.Contains("[string]$ModelOrigin = \"\"", script, StringComparison.Ordinal);
        Assert.Contains("[string]$ModelLicenseId = \"\"", script, StringComparison.Ordinal);
        Assert.Contains("Pinned -Fetch has its own model origin and licence", script, StringComparison.Ordinal);
        Assert.Contains("This NoModel edition has NO chat model", script, StringComparison.Ordinal);
        Assert.Contains("No runtime setup or Internet needed.", script, StringComparison.Ordinal);
        Assert.Contains("Local GGUF included:", script, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray()));
    }
}
