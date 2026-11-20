using System.Net.Http.Json;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Save backups mirrored to a folder the owner picks (a cloud-synced folder, a NAS or a USB drive). Real: the window, the Core and
/// the files copied. The mirror folder is an ordinary temp folder standing in for the cloud client's folder; the system folder
/// picker is not driven, so the folder is chosen through the Core's own endpoint (the same one the picker calls).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SaveBackupSyncUiTests
{
    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public async Task A_save_backup_is_copied_to_the_chosen_folder_from_the_window_and_the_folder_can_be_turned_off()
    {
        const string name = nameof(A_save_backup_is_copied_to_the_chosen_folder_from_the_window_and_the_folder_can_be_turned_off);
        UiTestApplication.WaitForCorePortReleased();
        using var app = new UiTestApplication(name);
        try
        {
            string mirror = Path.Combine(app.DataRoot, "cloud-folder");
            string saves = Path.Combine(app.DataRoot, "game-saves");
            Directory.CreateDirectory(mirror);
            Directory.CreateDirectory(saves);
            File.WriteAllText(Path.Combine(saves, "slot1.sav"), "level=7");

            string bootstrap = Path.Combine(app.DataRoot, "server", "bootstrap.json");
            UiWait.For(() => File.Exists(bootstrap), "the Core is up", TimeSpan.FromSeconds(90));
            using JsonDocument boot = JsonDocument.Parse(File.ReadAllText(bootstrap));
            string endpoint = boot.RootElement.GetProperty("endpoint").GetString()!.TrimEnd('/');
            string token = boot.RootElement.GetProperty("token").GetString()!;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("X-PaRuX-Token", token);
            (await http.PostAsJsonAsync(endpoint + "/api/v1/gaming/save-backups", new { SourceFolder = saves, Label = "Slot 1" })).EnsureSuccessStatusCode();
            (await http.PutAsJsonAsync(endpoint + "/api/v1/gaming/save-backups/sync", new { Folder = mirror })).EnsureSuccessStatusCode();

            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "Navigation_GameLibrary_Item", ControlType.ListItem));

            var status = UiWait.ById(app.MainWindow, "GameSaves_Sync_Status", ControlType.Text);
            UiWait.For(() => Text(status).Contains(mirror, StringComparison.OrdinalIgnoreCase), "the chosen folder is shown", TimeSpan.FromSeconds(30));
            foreach (string id in new[] { "GameSaves_Sync_Choose", "GameSaves_Sync_Now", "GameSaves_Sync_Off" })
                Assert.False(string.IsNullOrWhiteSpace(Text(UiWait.ById(app.MainWindow, id, ControlType.Button))), id + " has a name");

            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "GameSaves_Sync_Now", ControlType.Button));
            UiWait.For(() => Text(status).Contains("1", StringComparison.Ordinal) && Text(status).StartsWith("Done", StringComparison.Ordinal),
                "the sync reports one copied backup", TimeSpan.FromSeconds(60));
            string[] copied = Directory.GetFiles(Path.Combine(mirror, "PaRuX-save-backups"));
            Assert.Equal(2, copied.Length);   // the archive and its manifest
            Assert.Contains(copied, f => f.EndsWith(".zip", StringComparison.Ordinal));

            new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, "GameSaves_Sync_Off", ControlType.Button));
            UiWait.For(() => Text(status).Contains("No sync folder", StringComparison.Ordinal), "turning it off is reflected", TimeSpan.FromSeconds(30));
            Assert.False(UiWait.ById(app.MainWindow, "GameSaves_Sync_Now", ControlType.Button).Properties.IsEnabled.ValueOrDefault);
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
