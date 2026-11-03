using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GrapheneBrandingContractTests
{
    private static string Root()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln"))) root = root.Parent;
        return root?.FullName ?? throw new DirectoryNotFoundException("PaRuX.sln not found.");
    }

    [Fact]
    public void Graphene_preview_generator_is_separate_from_shipped_icons()
    {
        var root = Root();
        string source = File.ReadAllText(Path.Combine(root, "tools", "branding", "preview-graphene-orbit.ps1"));
        Assert.Contains("prx-orbit-approved.png", source);
        Assert.Contains("artifacts/branding/graphene-preview", source);
        Assert.Contains("Refusing to write a branding preview under the shipped Assets directory.", source);
        Assert.DoesNotContain("Write-PrxIcon", source);
        Assert.DoesNotContain("parux.ico", source);
        Assert.DoesNotContain("prx-orbit-tray.ico", source);
        foreach (int size in new[] { 16, 20, 24, 32, 48, 64, 128, 256 })
            Assert.Contains(size.ToString(), source);
    }

    [Fact]
    public void Approved_asset_and_original_icon_generator_are_still_authoritative()
    {
        var root = Root();
        string generator = File.ReadAllText(Path.Combine(root, "tools", "branding", "generate-prx-assets.ps1"));
        Assert.Contains("prx-orbit-approved.png", generator);
        Assert.Contains("parux.ico", generator);
        Assert.Contains("prx-orbit-tray.ico", generator);
    }
}
