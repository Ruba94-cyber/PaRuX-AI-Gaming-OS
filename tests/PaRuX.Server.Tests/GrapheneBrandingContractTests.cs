using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GrapheneBrandingContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PaRuX.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PaRuX.sln not found.");
    }

    [Fact]
    public void Active_windows_shell_and_quick_chat_use_distinct_px_orbit_marks()
    {
        var root = Root();
        string theme = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Styles", "Theme.xaml"));
        string project = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "PaRuX.App.csproj"));
        string bubble = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Windows", "BubbleWindow.xaml"));
        Assert.Contains("px-orbit-app.png", theme);
        Assert.Contains("px-orbit-chat.png", theme);
        Assert.DoesNotContain("ms-appx:///Assets/prx-orbit-approved.png", theme);
        Assert.Contains("PxOrbitChatMarkTemplate", bubble);
        Assert.Contains("CollapsedStateRing", bubble);
        Assert.Contains("px-orbit-app.png", project);
        Assert.Contains("px-orbit-chat.png", project);
        Assert.DoesNotContain("<Content Include=\"Assets\\prx-orbit-approved.png\"", project);
    }

    [Fact]
    public void App_and_tray_icons_have_valid_ico_headers()
    {
        var root = Root();
        foreach (var icon in new[] { "parux.ico", "prx-orbit-tray.ico" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, "src", "PaRuX.App", "Assets", icon));
            Assert.True(bytes.Length > 100);
            Assert.Equal(0, bytes[0]);
            Assert.Equal(0, bytes[1]);
            Assert.Equal(1, bytes[2]);
            Assert.Equal(0, bytes[3]);
            Assert.True(bytes[4] >= 5);
        }
    }

    [Fact]
    public void Windows_icon_generator_uses_approved_distinct_artworks()
    {
        var root = Root();
        string script = File.ReadAllText(Path.Combine(root, "tools", "branding", "generate-prx-assets.ps1"));
        Assert.Contains("px-orbit-app.png", script);
        Assert.Contains("px-orbit-chat.png", script);
        Assert.DoesNotContain("prx-orbit-approved.png", script);
    }

    [Fact]
    public void Bubble_orbit_uses_graphene_palette_and_respects_reduced_motion()
    {
        var root = Root();
        string code = File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Windows", "BubbleWindow.xaml.cs"));
        Assert.Contains("AreAnimationsEnabled()", code);
        Assert.Contains("UISettings().AnimationsEnabled", code);
        Assert.Contains("BubbleVisualState.Working ? 1450 : 2400", code);
        Assert.DoesNotContain("r = 124; g = 58; b = 237", code);
        Assert.DoesNotContain("r = 37; g = 99; b = 235", code);
    }
}
