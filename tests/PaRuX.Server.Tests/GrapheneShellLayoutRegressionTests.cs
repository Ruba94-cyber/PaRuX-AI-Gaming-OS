using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GrapheneShellLayoutRegressionTests
{
    private static string Root()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PaRuX.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("PaRuX.sln not found.");
    }

    [Fact]
    public void Shell_keeps_two_modes_inside_one_graphene_header()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(Root(), "src", "PaRuX.App", "MainWindow.xaml"));
        var host = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "ModeTabsHost"));
        Assert.Equal("{ThemeResource PaRuXGrapheneHeaderBrush}", (string?)host.Attribute("Background"));
        var tabs = Assert.Single(host.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "ModeTabs"));
        Assert.Equal(2, tabs.Elements().Count(e => e.Name.LocalName == "SelectorBarItem"));
        Assert.Contains(tabs.Elements(), e => (string?)e.Attribute(x + "Name") == "ModeTabAiItem");
        Assert.Contains(tabs.Elements(), e => (string?)e.Attribute(x + "Name") == "ModeTabGamingItem");
        var pane = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "NavView"));
        Assert.Equal("{ThemeResource PaRuXGrapheneSidebarBrush}", (string?)pane.Attribute("Background"));
    }

    [Fact]
    public void Chat_avatar_and_settings_rail_use_semantic_graphene()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var chat = XDocument.Load(Path.Combine(Root(), "src", "PaRuX.App", "Views", "ChatView.xaml"));
        var avatar = Assert.Single(chat.Descendants().Where(e => (string?)e.Attribute(x + "Key") == "PaRuXAvatarStyle"));
        Assert.Contains(avatar.Elements(), e => (string?)e.Attribute("Property") == "Background" &&
            (string?)e.Attribute("Value") == "{ThemeResource PaRuXGrapheneSelectedBrush}");
        var settings = XDocument.Load(Path.Combine(Root(), "src", "PaRuX.App", "Views", "SettingsView.xaml"));
        var rail = Assert.Single(settings.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "SettingsRailScroll"));
        Assert.Equal("{ThemeResource PaRuXGrapheneSidebarBrush}", (string?)rail.Attribute("Background"));
    }
}
