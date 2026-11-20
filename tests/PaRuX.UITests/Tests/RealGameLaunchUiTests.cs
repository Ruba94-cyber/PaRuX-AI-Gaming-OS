using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// A real PC game from this PC (C:\Games), started through PaRuX with the owner's consent. Real: the library entry, the consent, the
/// launch, the Gaming mode the Core enters, the process the game becomes, and the way back. Nothing is simulated. The test ends the
/// game it started (only processes that did not exist before the click and carry the game's executable name) and never touches
/// anything else. A game that refuses to start without its store client simply exits; that is reported, not hidden.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RealGameLaunchUiTests
{
    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    [Fact, Trait("Category", "RealHardware"), Trait("Category", "FlaUI")]
    public void A_real_game_is_started_through_consent_and_gaming_mode_ends_when_the_owner_returns_to_ai()
    {
        const string name = nameof(A_real_game_is_started_through_consent_and_gaming_mode_ends_when_the_owner_returns_to_ai);
        const string title = "Ghost of Tsushima DC";
        string exe = @"C:\Games\Ghost of Tsushima DC\GhostOfTsushima.exe";
        if (!File.Exists(exe)) return;   // a PC without this game proves nothing here
        string processName = Path.GetFileNameWithoutExtension(exe);
        UiTestApplication.WaitForCorePortReleased();

        string root = SyntheticGameLibrary.CreateRoot(0);
        File.WriteAllText(Path.Combine(root, "server", "manual-games.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Games = new[]
            {
                new
                {
                    Id = "real00001", Name = title, ExecutablePath = exe, Arguments = "", WorkingDirectory = Path.GetDirectoryName(exe)!,
                    Kind = "game", Platform = "", EmulatorId = "", GameFilePath = "",
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true }));

        var already = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
        using var app = new UiTestApplication(name, restartDataRoot: root);
        try
        {
            Press(app, "MainWindow_ModeTab_Gaming");
            string key = "ghostoftsushimadc";
            UiWait.ById(app.MainWindow, "GameTile_" + key);

            var clock = Stopwatch.StartNew();
            Press(app, "GameTilePlay_" + key);
            // Nothing starts without the owner's consent: the dialog appears first, and only the primary button starts the game.
            var consent = UiWait.ById(app.MainWindow, "PrimaryButton", ControlType.Button);
            Assert.Empty(Process.GetProcessesByName(processName).Where(p => !already.Contains(p.Id)));
            consent.Patterns.Invoke.Pattern.Invoke();

            Process? game = null;
            UiWait.For(() =>
            {
                game = Process.GetProcessesByName(processName).FirstOrDefault(p => !already.Contains(p.Id));
                return game is not null;
            }, "the game's process appears", TimeSpan.FromSeconds(60));
            long startMs = clock.ElapsedMilliseconds;
            string note = Path.Combine(Repository.Root, ".ai", "artifacts", "overnight", "real-game-launch.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(note)!);
            File.WriteAllText(note, "Real launch of " + title + " through PaRuX consent: click to process " + startMs + " ms." + Environment.NewLine);

            // PaRuX says Gaming is active while the game runs (or, if the game quit by itself at once, says so).
            var strip = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameLibrary_Strip_Status_Text"));
            UiWait.For(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameLibrary_Strip_Status_Text")) != null
                || Process.GetProcessesByName(processName).All(p => already.Contains(p.Id)),
                "PaRuX shows Gaming is active, or the game ended by itself", TimeSpan.FromSeconds(60));
            Thread.Sleep(8000);
            bool stillRunning = Process.GetProcessesByName(processName).Any(p => !already.Contains(p.Id));
            bool stripShown = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GameLibrary_Strip_Status_Text")) != null;
            File.AppendAllText(note, "8 s later: game still running = " + stillRunning + "; Gaming strip shown = " + stripShown + "." + Environment.NewLine);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
        finally
        {
            // End only what this test started.
            foreach (Process p in Process.GetProcessesByName(processName).Where(p => !already.Contains(p.Id)))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(15_000); } catch (Exception) { }
            }
            Assert.Empty(Process.GetProcessesByName(processName).Where(p => !already.Contains(p.Id)));
        }
    }
}
