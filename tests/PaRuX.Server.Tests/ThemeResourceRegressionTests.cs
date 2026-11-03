using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ThemeResourceRegressionTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Light", "AccentButtonForeground", "AccentButtonBackground")]
    [InlineData("Light", "AccentButtonForegroundPointerOver", "AccentButtonBackgroundPointerOver")]
    [InlineData("Light", "AccentButtonForegroundPressed", "AccentButtonBackgroundPressed")]
    [InlineData("Dark", "AccentButtonForeground", "AccentButtonBackground")]
    [InlineData("Dark", "AccentButtonForegroundPointerOver", "AccentButtonBackgroundPointerOver")]
    [InlineData("Dark", "AccentButtonForegroundPressed", "AccentButtonBackgroundPressed")]
    [InlineData("Light", "ToggleButtonForegroundChecked", "ToggleButtonBackgroundChecked")]
    [InlineData("Light", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPointerOver")]
    [InlineData("Light", "ToggleButtonForegroundCheckedPressed", "ToggleButtonBackgroundCheckedPressed")]
    [InlineData("Dark", "ToggleButtonForegroundChecked", "ToggleButtonBackgroundChecked")]
    [InlineData("Dark", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPointerOver")]
    [InlineData("Dark", "ToggleButtonForegroundCheckedPressed", "ToggleButtonBackgroundCheckedPressed")]
    public void Interactive_text_contrast_meets_normal_text_AA(string theme, string foreground, string background)
    {
        var resources = ReadTheme(theme);
        var fg = WcagColor.Parse((string?)resources[foreground].Attribute("Color") ?? throw new InvalidDataException(foreground));
        var bg = WcagColor.Parse((string?)resources[background].Attribute("Color") ?? throw new InvalidDataException(background));
        Assert.Equal(255, fg.A);
        Assert.Equal(255, bg.A);
        double ratio = WcagContrastCalculator.CalculateContrastRatio(fg, bg);
        Assert.True(ratio >= 4.5, $"{theme}: {foreground} on {background} has contrast {ratio:F2}:1; expected at least 4.5:1.");
    }

    [Fact]
    public void Light_and_dark_expose_identical_unique_keys_and_high_contrast_retains_parux_keys()
    {
        var light = ReadTheme("Light");
        var dark = ReadTheme("Dark");
        var highContrast = ReadTheme("HighContrast");
        Assert.Equal(light.Keys.OrderBy(x => x), dark.Keys.OrderBy(x => x));
        Assert.Empty(light.Keys.Where(x => x.StartsWith("PaRuX", StringComparison.Ordinal) && !highContrast.ContainsKey(x)));
    }

    [Fact]
    public void Quick_chat_bubble_uses_flat_theme_resources_instead_of_decorative_gradients()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) current = current.Parent;
        if (current == null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        var doc = XDocument.Load(Path.Combine(current.FullName, "src", "PaRuX.App", "Windows", "BubbleWindow.xaml"));
        Assert.DoesNotContain(doc.Descendants(), element => element.Name.LocalName == "LinearGradientBrush");
        XElement collapsed = doc.Descendants().Single(element =>
            element.Name.LocalName == "Grid" && (string?)element.Attribute(Xaml + "Name") == "CollapsedView");
        XElement label = collapsed.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "CollapsedStatusText");
        Assert.Equal("{ThemeResource PaRuXTextPrimaryBrush}", (string?)label.Attribute("Foreground"));
    }

    [Fact]
    public void Graphene_dark_palette_uses_neutral_surfaces_and_satin_titanium()
    {
        var dark = ReadTheme("Dark");
        Assert.Equal("#07090C", (string?)dark["PaRuXSurfaceSolidBrush"].Attribute("Color"));
        Assert.Equal("#0A0D11", (string?)dark["PaRuXGrapheneSidebarBrush"].Attribute("Color"));
        Assert.Equal("#0D1116", (string?)dark["PaRuXSurfaceCardBrush"].Attribute("Color"));
        Assert.Equal("#B7C0C9", (string?)dark["AccentButtonBackground"].Attribute("Color"));
        Assert.Equal("#B89B6A", (string?)dark["PaRuXChampagneBrush"].Attribute("Color"));
        var text = WcagColor.Parse((string?)dark["PaRuXTextSecondaryBrush"].Attribute("Color")!);
        var background = WcagColor.Parse((string?)dark["PaRuXSurfaceCardBrush"].Attribute("Color")!);
        Assert.True(WcagContrastCalculator.CalculateContrastRatio(text, background) >= 4.5);
    }

    [Fact]
    public void Legacy_neon_colors_are_absent_from_shared_WinUI_theme()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) current = current.Parent;
        if (current is null) throw new DirectoryNotFoundException("PaRuX.sln not found.");
        string xaml = File.ReadAllText(Path.Combine(current.FullName, "src", "PaRuX.App", "Styles", "Theme.xaml"));
        Assert.DoesNotContain("#26B7C6", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#79CBD4", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#006C76", xaml, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, XElement> ReadTheme(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) current = current.Parent;
        if (current == null) throw new DirectoryNotFoundException("PaRuX.sln was not found above the test output directory.");
        var doc = XDocument.Load(Path.Combine(current.FullName, "src", "PaRuX.App", "Styles", "Theme.xaml"));
        var dictionary = doc.Descendants().Single(x => x.Name.LocalName == "ResourceDictionary" && (string?)x.Attribute(Xaml + "Key") == name);
        // ToDictionary fails on duplicate keys, matching XAML resource semantics.
        return dictionary.Elements().Where(x => x.Attribute(Xaml + "Key") != null)
            .ToDictionary(x => (string)x.Attribute(Xaml + "Key")!, StringComparer.Ordinal);
    }
}
