using System;
using System.IO;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure rules behind the Microsoft Store (MSIX) edition: where a packaged process keeps its
/// data and when the in-app self-updater is allowed. Runtime detection itself is exercised by the
/// real packaged-install steps in docs/store/SUBMISSION_CHECKLIST.md.</summary>
public sealed class PackageIdentityTests
{
    private const string Pfn = "Example.App_0123456789abc";
    private static readonly string Local = Path.Combine(Path.GetTempPath(), "parux-store-local");

    [Theory]
    [InlineData("Example.App_0123456789abc", true)]
    [InlineData("Example-App.Two_8wekyb3d8bbwe", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("NoUnderscore", false)]
    [InlineData("_leading", false)]
    [InlineData("trailing_", false)]
    [InlineData("two_under_scores", false)]
    [InlineData("..\\evil_abc", false)]
    [InlineData("a/b_abc", false)]
    [InlineData("a..b_abc", false)]
    public void FamilyNameValidation_RejectsAnythingThatCouldEscapeThePackagesFolder(string? name, bool valid)
        => Assert.Equal(valid, PackageIdentity.IsValidFamilyName(name));

    [Fact]
    public void PackagedDataRoot_IsTheRealUnvirtualizedLocalStateFolder()
    {
        string expected = Path.Combine(Local, "Packages", Pfn, "LocalState", "PaRuX");
        Assert.Equal(expected, PackageIdentity.PackagedDataRoot(Local, Pfn));
        Assert.Equal(Path.Combine(Local, "Packages", Pfn, "LocalState", "LocalAI"),
            PackageIdentity.PackagedLocalAiStateRoot(Local, Pfn));
    }

    [Fact]
    public void PackagedDataRoot_RefusesInvalidFamilyName()
        => Assert.Throws<ArgumentException>(() => PackageIdentity.PackagedDataRoot(Local, "..\\x_y"));

    [Fact]
    public void DataRoot_UnpackagedKeepsTheLegacyLocation()
    {
        string unpackaged = PaRuXDataRoot.ResolveDefaultLocation(Path.Combine(Local, "app"), null, null);
        Assert.Equal(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaRuX"), unpackaged);
    }

    [Fact]
    public void DataRoot_PackagedUsesPackageLocalState_AndExplicitOverrideStillWins()
    {
        string packaged = PaRuXDataRoot.ResolveDefaultLocation(Path.Combine(Local, "app"), null, Pfn);
        Assert.EndsWith(Path.Combine("Packages", Pfn, "LocalState", "PaRuX"), packaged);

        string overrideRoot = Path.Combine(Path.GetTempPath(), "parux-explicit-override");
        Assert.Equal(Path.GetFullPath(overrideRoot),
            PaRuXDataRoot.ResolveDefaultLocation(Path.Combine(Local, "app"), overrideRoot, Pfn));
    }

    [Fact]
    public void DataRoot_InvalidFamilyNameFallsBackToTheLegacyLocation()
    {
        string result = PaRuXDataRoot.ResolveDefaultLocation(Path.Combine(Local, "app"), null, "bad");
        Assert.Equal(PaRuXDataRoot.ResolveDefaultLocation(Path.Combine(Local, "app"), null, null), result);
    }

    [Fact]
    public void LocalAiState_PackagedUsesPackageLocalState()
    {
        string packaged = PaRuXLocalAiStateRoot.Resolve(Path.Combine(Local, "app"), null, Pfn);
        Assert.EndsWith(Path.Combine("Packages", Pfn, "LocalState", "LocalAI"), packaged);
        Assert.EndsWith("LocalAI", PaRuXLocalAiStateRoot.Resolve(Path.Combine(Local, "app"), null, null));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SelfUpdate_IsDisabledOnlyForPackagedProcesses(bool packaged, bool allowed)
        => Assert.Equal(allowed, PackageIdentity.AllowsInAppSelfUpdateFor(packaged));

    [Fact]
    public void UnpackagedTestProcess_HasNoPackageIdentity()
    {
        Assert.False(PackageIdentity.IsPackaged);
        Assert.Null(PackageIdentity.FamilyName);
        Assert.True(PackageIdentity.AllowsInAppSelfUpdate);
    }
}
