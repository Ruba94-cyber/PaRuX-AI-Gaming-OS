using System.Globalization;
using System.Text.RegularExpressions;
using PaRuX.Services;
using PaRuX.Services.Integrations;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// L10N follow-up: the XAML hook contract (UiLocalizationHookTests) cannot see text that is built in C#. This guards the
/// code-built UI: a ratchet over literal user-visible English in PaRuX.App sources, plus Italian behaviour of the areas that
/// were found English-only (Integrations, shadow-apply recovery dialog).
/// </summary>
public sealed class CodeBuiltUiLocalizationTests
{
    // Property assignments that put text in front of a person.
    private static readonly Regex Assignment = new(
        @"\b(Text|Content|Header|PlaceholderText|Title|PrimaryButtonText|CloseButtonText|SecondaryButtonText|Description|Label)\s*=\s*(\$?""(?:[^""\\]|\\.)*"")|AutomationProperties\.SetName\([^,]+,\s*(\$?""(?:[^""\\]|\\.)*"")\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex Sentence = new(@"[A-Za-z]{3,}.*\s[A-Za-z]{2,}|^[A-Z][a-z]{3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // Known leftovers that are NOT localizable UI text today: debug-only UI fixtures (built only in Debug), model/prompt text sent to
    // an AI, brand strings and format templates. A file may never exceed its count and no other file may add a literal: new
    // user-visible text goes through T(...)/UiText.Pick(...). Lower a number when a literal is removed.
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Views/ModelHubView.cs"] = 42,                  // "UI test:" / "UI fixture:" debug fixtures
        ["Views/OnboardingView.FirstModel.cs"] = 6,      // fixture models
        ["Views/GamingDevicesPanel.cs"] = 3,             // "{w} x {h} @ {hz} Hz" and device-id format templates
        ["Services/ComputerUseAgentService.cs"] = 2,     // evidence labels, not shown as UI text
        ["MainWindow.xaml.cs"] = 1,                      // product title
        ["Services/BrowserDevToolsService.cs"] = 1,      // prompt sent to the local model
        ["Services/ModalityChatPresenter.cs"] = 1,       // English source constant; the dialog uses the localized pair next to it
        ["Services/UiVerificationWorkerService.cs"] = 1, // evidence label
        ["Views/ChatView.NativeCodingHandoff.cs"] = 1,   // fixture
        ["Views/Loc.cs"] = 1,                            // doc comment sample
        ["Views/ModelRoutingPanel.cs"] = 1,              // fixture
        ["Views/ToolsAutomationView.cs"] = 1,            // JSON example
        ["Views/UniversalLibraryPanel.cs"] = 1,          // path and size template
    };

    [Fact]
    public void Code_built_UI_text_does_not_grow_English_only_literals()
    {
        string appRoot = Path.Combine(FindRepositoryRoot(), "src", "PaRuX.App");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(appRoot, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
            int count = 0;
            foreach (string line in File.ReadLines(file))
            {
                foreach (Match match in Assignment.Matches(line))
                {
                    string literal = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                    string body = literal.Trim('$', '"');
                    if (!Sentence.IsMatch(body)) continue;
                    string before = line[Math.Max(0, match.Index - 12)..match.Index];
                    if (before.Contains("T(", StringComparison.Ordinal) || before.Contains("UiText", StringComparison.Ordinal)
                        || before.Contains("Pick(", StringComparison.Ordinal)) continue;
                    count++;
                }
            }
            if (count > 0) counts[relative] = count;
        }

        var growth = new List<string>();
        foreach ((string file, int count) in counts)
            if (count > Baseline.GetValueOrDefault(file))
                growth.Add(file + ": " + count + " English-only literal(s), allowed " + Baseline.GetValueOrDefault(file)
                    + ". Route user-visible text through T(...) or UiText.Pick(...).");
        Assert.True(growth.Count == 0, string.Join("\n", growth));
    }

    // Sets the culture of THIS thread only (never the process-wide default), so tests running in parallel keep their own language.
    private static T InCulture<T>(string culture, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture == UiLanguage.Italian ? "it-IT" : "en-US");
            return action();
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Integration_status_labels_follow_the_UI_language_and_cover_every_status()
    {
        foreach (ClientIntegrationStatus status in Enum.GetValues<ClientIntegrationStatus>())
        {
            string english = InCulture(UiLanguage.English, () => ClientIntegrationStatusText.Label(status));
            string italian = InCulture(UiLanguage.Italian, () => ClientIntegrationStatusText.Label(status));
            Assert.False(string.IsNullOrWhiteSpace(english));
            Assert.False(string.IsNullOrWhiteSpace(italian));
            Assert.NotEqual(english, italian);
        }
        Assert.Equal("Available", InCulture(UiLanguage.English, () => ClientIntegrationStatusText.Label(ClientIntegrationStatus.Available)));
        Assert.Equal("Disponibile", InCulture(UiLanguage.Italian, () => ClientIntegrationStatusText.Label(ClientIntegrationStatus.Available)));
    }

    [Fact]
    public void Shadow_recovery_dialog_texts_have_an_Italian_version_for_every_string()
    {
        (string Name, Func<string> Get)[] texts =
        {
            ("DialogTitle", () => ShadowRecoveryDialogModel.DialogTitle),
            ("RestoreButtonText", () => ShadowRecoveryDialogModel.RestoreButtonText),
            ("KeepButtonText", () => ShadowRecoveryDialogModel.KeepButtonText),
            ("CancelButtonText", () => ShadowRecoveryDialogModel.CancelButtonText),
            ("OpenFolderButtonText", () => ShadowRecoveryDialogModel.OpenFolderButtonText),
            ("CloseButtonText", () => ShadowRecoveryDialogModel.CloseButtonText),
            ("KeepConfirmTitle", () => ShadowRecoveryDialogModel.KeepConfirmTitle),
            ("KeepConfirmMessage", () => ShadowRecoveryDialogModel.KeepConfirmMessage),
            ("KeepConfirmButtonText", () => ShadowRecoveryDialogModel.KeepConfirmButtonText),
            ("KeepDeclinedText", () => ShadowRecoveryDialogModel.KeepDeclinedText),
        };
        foreach ((string name, Func<string> get) in texts)
        {
            string english = InCulture(UiLanguage.English, get);
            string italian = InCulture(UiLanguage.Italian, get);
            Assert.False(string.IsNullOrWhiteSpace(italian), name);
            Assert.NotEqual(english, italian);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PaRuX.sln not found above the test output directory.");
    }
}
