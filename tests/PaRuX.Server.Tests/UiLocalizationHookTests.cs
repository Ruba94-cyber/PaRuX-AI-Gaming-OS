using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// L10N-01: the shared English/Italian pick (UiText) and the XAML hook contract (PaRuX.Views.Loc attached
/// properties). The XAML attributes are only applied at load time, so these tests parse every localized XAML
/// file and enforce the rules the runtime relies on: English text stays in the normal attribute, the Italian
/// attribute follows it, and no visible English attribute is left without an Italian companion.
/// </summary>
public sealed class UiLocalizationHookTests
{
    private const string LocNamespace = "using:PaRuX.Views";

    // English attribute -> Italian companion in the Loc hook.
    private static readonly (string English, string Italian)[] Pairs =
    {
        ("Text", "ItText"), ("Content", "ItContent"), ("Header", "ItHeader"),
        ("ToolTipService.ToolTip", "ItToolTip"), ("PlaceholderText", "ItPlaceholder"),
        ("AutomationProperties.Name", "ItName"), ("AutomationProperties.HelpText", "ItHelpText"),
        ("OnContent", "ItOnContent"), ("OffContent", "ItOffContent"), ("Title", "ItTitle"),
    };

    private static readonly string[] LocalizedXaml =
    {
        "Views/SettingsView.xaml",
        "Views/ChatView.xaml",
    };

    // Visible English values that are intentionally identical in Italian (brand names, product names, units,
    // example literals, language names). Anything else without an Italian companion fails the test.
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal)
    {
        "English", "Italiano", "Chat", "Incognito", "AI (Ctrl+1)", "Gaming", "Gaming (Ctrl+2)", "Model Hub", "Lifecycle Lab", "Runtime", "PaRuX", "Console / TV", "GAMING", "INTERNET", "PERSONAS", "AVATAR STUDIO", "Auto", "Standard", "AUTO", "Provider", "Trigger", "Autopilot", "Dataset",
        "Benchmark", "Personas", "FPS", "GPU / VRAM", "VS Code", "OpenCode", "parux-code", "parux-reasoning",
        "parux-fast", "* (default)", "http://127.0.0.1:11434", "https://your-gateway.example.com/v1",
        "openai/gpt-oss-120b", "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit", "PaRuX · ", "PaRuX AI & Gaming OS",
        "C:\\work\\binary-analysis", "C:\\Downloads\\example.parux-plugin",
    };

    [Fact]
    public void Pick_returns_English_by_default_and_when_italian_is_off()
    {
        Assert.Equal("Save", UiText.Pick("Save", "Salva", italian: false));
    }

    [Fact]
    public void Pick_returns_Italian_when_italian_is_selected()
    {
        Assert.Equal("Salva", UiText.Pick("Save", "Salva", italian: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Pick_falls_back_to_English_when_the_Italian_slot_is_empty(string? italianText)
    {
        Assert.Equal("Save", UiText.Pick("Save", italianText, italian: true));
    }

    [Fact]
    public void Pick_follows_the_UI_culture_set_by_UiLanguage()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        CultureInfo? previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            UiLanguage.Apply(UiLanguage.English);
            Assert.False(UiText.IsItalian);
            Assert.Equal("Save", UiText.Pick("Save", "Salva"));

            UiLanguage.Apply(UiLanguage.Italian);
            Assert.True(UiText.IsItalian);
            Assert.Equal("Salva", UiText.Pick("Save", "Salva"));
            Assert.Equal("Save", UiText.Pick("Save", ""));

            // Anything that is not explicitly Italian stays English (English is the product default).
            UiLanguage.Apply("fr");
            Assert.False(UiText.IsItalian);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Localized_xaml_declares_the_hook_namespace_and_parses()
    {
        foreach (string file in LocalizedXaml)
        {
            XDocument doc = Load(file);
            XAttribute? ns = doc.Root!.Attributes().FirstOrDefault(a => a.IsNamespaceDeclaration && a.Name.LocalName == "loc");
            Assert.True(ns != null && ns.Value == LocNamespace, file + " must declare xmlns:loc=\"" + LocNamespace + "\"");
        }
    }

    [Fact]
    public void Every_Italian_attribute_follows_its_non_empty_English_attribute()
    {
        XNamespace loc = LocNamespace;
        foreach (string file in LocalizedXaml)
        {
            foreach (XElement element in Load(file).Descendants().Prepend(Load(file).Root!))
            {
                List<XAttribute> attributes = element.Attributes().ToList();
                foreach ((string english, string italian) in Pairs)
                {
                    XAttribute? it = attributes.FirstOrDefault(a => a.Name == loc + ("Loc." + italian));
                    if (it == null) continue;
                    XAttribute? en = attributes.FirstOrDefault(a => a.Name.NamespaceName == "" && a.Name.LocalName == english);
                    Assert.True(en != null, file + ": <" + element.Name.LocalName + "> has Loc." + italian + " but no " + english);
                    Assert.True(attributes.IndexOf(en!) < attributes.IndexOf(it), file + ": Loc." + italian + " must come after " + english + " (attributes apply in document order)");
                    Assert.False(string.IsNullOrWhiteSpace(it.Value), file + ": empty Italian text for \"" + en!.Value + "\"");
                    Assert.False(it.Value.StartsWith('{'), file + ": Italian text must not be a markup extension");
                }
            }
        }
    }

    [Fact]
    public void Every_visible_English_attribute_has_an_Italian_companion()
    {
        XNamespace loc = LocNamespace;
        var missing = new List<string>();
        foreach (string file in LocalizedXaml)
        {
            XDocument doc = Load(file);
            foreach (XElement element in doc.Descendants().Prepend(doc.Root!))
            {
                // Window titles are not DependencyObjects; they are set from code.
                if (element.Name.LocalName == "Window") continue;
                foreach ((string english, string italian) in Pairs)
                {
                    XAttribute? en = element.Attributes().FirstOrDefault(a => a.Name.NamespaceName == "" && a.Name.LocalName == english);
                    if (en == null || en.Value.StartsWith('{') || !HasWord(en.Value)) continue;
                    if (element.Attribute(loc + ("Loc." + italian)) != null) continue;
                    if (SameInBothLanguages.Contains(en.Value)) continue;
                    missing.Add(file + " " + english + "=\"" + Shorten(en.Value) + "\"");
                }
            }
        }
        Assert.True(missing.Count == 0, "English-only XAML text:\n" + string.Join("\n", missing.Take(40)));
    }

    [Fact]
    public void The_hook_never_targets_automation_ids()
    {
        XNamespace loc = LocNamespace;
        foreach (string file in LocalizedXaml)
        {
            Assert.DoesNotContain(Load(file).Descendants().SelectMany(e => e.Attributes()),
                a => a.Name.Namespace == loc && a.Name.LocalName.Contains("AutomationId", StringComparison.Ordinal));
        }
    }

    private static bool HasWord(string value)
    {
        int run = 0;
        foreach (char c in value)
        {
            run = char.IsAsciiLetter(c) ? run + 1 : 0;
            if (run >= 3) return true;
        }
        return false;
    }

    private static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "...";

    private static XDocument Load(string relative) =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PaRuX.sln not found above the test output directory.");
    }
}
