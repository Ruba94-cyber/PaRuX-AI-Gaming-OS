using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Gaming home: the Quick Resume panel (pause/resume of an application) and the opt-in "Covers &amp; info" toggle.
/// Quick Resume only ever acts on a throw-away PowerShell form window started by the test itself and always resumed or
/// killed in finally; the metadata consent dialog is never accepted, so nothing is looked up online.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class QuickResumeAndMetadataUiTests
{
    private const string GamingTab = "MainWindow_ModeTab_Gaming";
    private static readonly Regex ActionId = new(@"^QuickResume_(Pause|Resume)_(\d+)$", RegexOptions.Compiled);

    // Names QuickResumePolicy refuses, spelled out here on purpose: the test must not depend on the policy it verifies.
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "sihost", "taskhostw", "RuntimeBroker",
        "ApplicationFrameHost", "MsMpEng", "conhost", "taskmgr", "PaRuX", "PaRuX.Server", "PaRuX.Cli", "PaRuX.UpdateHelper",
        "PaRuX.WinMlWorker", "llama-server"
    };

    private static AutomationElement Find(Func<AutomationElement?> find, string description)
    {
        AutomationElement? element = null;
        UiWait.For(() => (element = find()) != null, description);
        return element!;
    }

    private static AutomationElement ById(UiTestApplication app, string id) =>
        Find(() => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id)), id);

    private static AutomationElement? TryById(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static void WaitForCoreIdle()
    {
        UiWait.For(() => !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 51123),
            "previous Core released port 51123", TimeSpan.FromSeconds(20));
    }

    private static void OpenGamingHome(UiTestApplication app)
    {
        var page = new InstallProgressPage(app);
        page.Invoke(ById(app, GamingTab));
        ById(app, "GameHome_Sort");
        var add = ById(app, "GameLibrary_Add_Button");
        UiWait.For(() => add.Properties.IsEnabled.ValueOrDefault, "library finished loading");
    }

    private static void ScrollTo(AutomationElement element)
    {
        try { element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch (Exception) { /* best effort */ }
    }

    /// <summary>Scrolls the panel into view and waits for its first refresh to finish (a status text is shown).</summary>
    private static AutomationElement OpenQuickResume(UiTestApplication app)
    {
        var heading = ById(app, "QuickResume_Heading");
        ScrollTo(heading);
        UiWait.For(() => !string.IsNullOrWhiteSpace(ById(app, "QuickResume_Status_Text").Properties.Name.ValueOrDefault),
            "Quick Resume status text");
        return heading;
    }

    private static string Status(UiTestApplication app) => ById(app, "QuickResume_Status_Text").Name ?? string.Empty;

    private static List<(string Kind, int Pid)> Actions(UiTestApplication app)
    {
        var list = new List<(string, int)>();
        var root = TryById(app, "QuickResume_List");
        if (root == null) return list;
        foreach (var element in root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
        {
            Match match = ActionId.Match(element.Properties.AutomationId.ValueOrDefault ?? string.Empty);
            if (match.Success) list.Add((match.Groups[1].Value, int.Parse(match.Groups[2].Value)));
        }
        return list;
    }

    private static void Refresh(UiTestApplication app)
    {
        var refresh = ById(app, "QuickResume_Refresh_Button");
        ScrollTo(refresh);
        new InstallProgressPage(app).Invoke(refresh);
    }

    /// <summary>
    /// A harmless windowed process owned by the test: a PowerShell WinForms window with a unique title. It is started by
    /// this test and is the only process this test ever pauses.
    /// </summary>
    private sealed class WindowFixture : IDisposable
    {
        internal Process Process { get; }
        internal string Title { get; }

        internal WindowFixture(string title)
        {
            Title = title;
            string script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
                            "$f.Text = '" + title + "'; $f.Width = 320; $f.Height = 160; [System.Windows.Forms.Application]::Run($f)";
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            Process = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -STA -EncodedCommand " + encoded)
            { UseShellExecute = false, CreateNoWindow = true })!;
            UiWait.For(() =>
            {
                Process.Refresh();
                return !Process.HasExited && Process.MainWindowTitle == title;
            }, "fixture window " + title, TimeSpan.FromSeconds(30));
        }

        /// <summary>Threads of the fixture that Windows reports as suspended.</summary>
        internal int SuspendedThreads()
        {
            Process.Refresh();
            int count = 0;
            foreach (ProcessThread thread in Process.Threads)
            {
                try
                {
                    if (thread.ThreadState == System.Diagnostics.ThreadState.Wait && thread.WaitReason == ThreadWaitReason.Suspended) count++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* thread ended */ }
            }
            return count;
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    // A paused process must be resumed before it can be killed cleanly; killing a suspended one is also fine.
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(10000);
                }
            }
            catch (Exception) { /* already gone */ }
            Process.Dispose();
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void QuickResume_panel_is_below_the_library_and_refresh_keeps_it_safe()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(QuickResume_panel_is_below_the_library_and_refresh_keeps_it_safe));
        try
        {
            OpenGamingHome(app);
            var sort = ById(app, "GameHome_Sort");
            var heading = OpenQuickResume(app);
            Assert.Equal("Quick Resume", heading.Name);
            Assert.True(heading.BoundingRectangle.Top > sort.BoundingRectangle.Top ||
                        sort.Properties.IsOffscreen.ValueOrDefault,
                "the Quick Resume panel sits below the library toolbar");

            var refresh = ById(app, "QuickResume_Refresh_Button");
            var reviewMods = ById(app, "QuickResume_Mods_ReviewButton");
            var restoreMods = ById(app, "QuickResume_Mods_RestoreButton");
            var skinStudio = ById(app, "QuickResume_Mods_SkinStudioButton");
            Assert.False(string.IsNullOrWhiteSpace(skinStudio.Name), "local AI skin studio has an accessible label");
            Assert.True(skinStudio.Properties.IsEnabled.ValueOrDefault);
            UiWait.For(() => skinStudio.BoundingRectangle.Height >= 44,
                "Minecraft skin studio button has minimum touch target");

            Assert.False(string.IsNullOrWhiteSpace(reviewMods.Name), "review AI mods has a name");
            Assert.False(string.IsNullOrWhiteSpace(restoreMods.Name), "restore mod backups has a name");
            Assert.True(reviewMods.Properties.IsEnabled.ValueOrDefault);
            Assert.True(restoreMods.Properties.IsEnabled.ValueOrDefault);
            Assert.False(string.IsNullOrWhiteSpace(refresh.Name), "refresh has an accessible name");
            ScrollTo(refresh);
            UiWait.For(() => refresh.BoundingRectangle.Height >= 44, "refresh is at least 44px tall");
            // The list container is only exposed to UI Automation once it has rows; it is checked after the first listing.

            Refresh(app);
            UiWait.For(() => !string.IsNullOrWhiteSpace(Status(app)), "status after refresh");
            Assert.True(refresh.Properties.IsEnabled.ValueOrDefault, "refresh stays usable");

            // Nothing in the list may be a PaRuX process, the test host, or a protected Windows component.
            HashSet<int> own = OwnedPids();
            foreach (var (kind, pid) in Actions(app))
            {
                Assert.DoesNotContain(pid, own);
                string name = ProcessName(pid);
                Assert.False(ProtectedNames.Contains(name), "protected process listed: " + name + " (" + pid + ")");
            }
            // With no paused application there is nothing to resume in bulk.
            if (!Actions(app).Any(a => a.Kind == "Resume"))
            {
                var all = TryById(app, "QuickResume_ResumeAll_Button");
                Assert.True(all == null || all.Properties.IsOffscreen.ValueOrDefault, "no Resume all without paused apps");
            }
        }
        catch (Exception ex) { app.CaptureFailure(nameof(QuickResume_panel_is_below_the_library_and_refresh_keeps_it_safe), ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void QuickResume_pauses_and_resumes_a_test_window_and_refuses_system_windows()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(QuickResume_pauses_and_resumes_a_test_window_and_refuses_system_windows));
        string title = "PaRuXQuickResumeFixture" + Environment.ProcessId;
        using var fixture = new WindowFixture(title);
        int pid = fixture.Process.Id;
        try
        {
            OpenGamingHome(app);
            OpenQuickResume(app);

            // The fixture is a normal windowed app: it must be listed with a Pause action.
            UiWait.For(() =>
            {
                Refresh(app);
                return TryById(app, "QuickResume_Pause_" + pid) != null;
            }, "fixture window listed with a Pause action (pid " + pid + "; status '" + Status(app) + "'; listed "
                + string.Join(",", Actions(app).Select(a => a.Kind + a.Pid)) + ")", TimeSpan.FromSeconds(30));
            Assert.Equal(0, fixture.SuspendedThreads());

            // Protected Windows components and PaRuX itself are never offered, even though they own windows.
            HashSet<int> own = OwnedPids();
            foreach (var (_, otherPid) in Actions(app))
            {
                Assert.DoesNotContain(otherPid, own);
                Assert.False(ProtectedNames.Contains(ProcessName(otherPid)), "protected process listed: " + ProcessName(otherPid));
            }
            foreach (Process shell in Process.GetProcessesByName("explorer"))
            {
                using (shell)
                {
                    Assert.Null(TryById(app, "QuickResume_Pause_" + shell.Id));
                    Assert.Null(TryById(app, "QuickResume_Resume_" + shell.Id));
                }
            }

            // Pause: the row flips to Resume and Windows really reports the fixture's threads as suspended.
            ScrollTo(ById(app, "QuickResume_Pause_" + pid));
            new InstallProgressPage(app).Invoke(ById(app, "QuickResume_Pause_" + pid));
            UiWait.For(() => TryById(app, "QuickResume_Resume_" + pid) != null, "fixture shown as paused");
            Assert.Null(TryById(app, "QuickResume_Pause_" + pid));
            UiWait.For(() => fixture.SuspendedThreads() > 0, "fixture threads suspended by Windows");
            Assert.True(ById(app, "QuickResume_ResumeAll_Button").Properties.IsEnabled.ValueOrDefault);

            // Resume: the row returns to Pause and no thread stays suspended.
            ScrollTo(ById(app, "QuickResume_Resume_" + pid));
            new InstallProgressPage(app).Invoke(ById(app, "QuickResume_Resume_" + pid));
            UiWait.For(() => TryById(app, "QuickResume_Pause_" + pid) != null, "fixture shown as running again");
            UiWait.For(() => fixture.SuspendedThreads() == 0, "fixture threads running again");
            Assert.False(fixture.Process.HasExited, "resuming must not end the application");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(QuickResume_pauses_and_resumes_a_test_window_and_refuses_system_windows), ex); throw; }
        finally
        {
            // No paused process may outlive the test: resume through the Core if the UI left it paused, then the fixture is killed.
            try
            {
                if (!fixture.Process.HasExited && fixture.SuspendedThreads() > 0)
                {
                    var all = TryById(app, "QuickResume_ResumeAll_Button");
                    if (all != null) new InstallProgressPage(app).Invoke(all);
                    DateTime until = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < until && fixture.SuspendedThreads() > 0) Thread.Sleep(100);
                }
            }
            catch (Exception) { /* Dispose kills the fixture regardless. */ }
        }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void Covers_and_info_consent_cancel_leaves_it_off_and_contacts_nothing()
    {
        WaitForCoreIdle();
        using var app = new UiTestApplication(nameof(Covers_and_info_consent_cancel_leaves_it_off_and_contacts_nothing));
        try
        {
            OpenGamingHome(app);
            var toggle = ById(app, "GameHome_Metadata_Toggle");
            Assert.False(string.IsNullOrWhiteSpace(toggle.Name), "toggle has an accessible name");
            ScrollTo(toggle);
            UiWait.For(() => toggle.BoundingRectangle.Height >= 44, "toggle is at least 44px tall");
            Assert.Equal(ToggleState.Off, toggle.Patterns.Toggle.Pattern.ToggleState.Value);
            Assert.False(MetadataEnabled(app), "off by default");

            new InstallProgressPage(app).Invoke(toggle);
            var cancel = Find(() => app.MainWindow.FindFirstDescendant(
                cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button))), "consent dialog");
            Assert.NotNull(app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")));
            Assert.False(MetadataEnabled(app), "not enabled while the dialog is open");
            cancel.Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => TryById(app, "CloseButton") == null, "consent dialog closed");
            UiWait.For(() => ById(app, "GameHome_Metadata_Toggle").Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.Off,
                "toggle back off after Cancel");
            Assert.False(MetadataEnabled(app), "Cancel must not persist the option");
            Assert.False(File.Exists(Path.Combine(app.DataRoot, "server", "game-metadata.json")), "no metadata cache after Cancel");
        }
        catch (Exception ex) { app.CaptureFailure(nameof(Covers_and_info_consent_cancel_leaves_it_off_and_contacts_nothing), ex); throw; }
    }

    private static bool MetadataEnabled(UiTestApplication app) =>
        File.Exists(app.SettingsPath) &&
        Regex.IsMatch(File.ReadAllText(app.SettingsPath), "\"GameMetadataEnabled\"\\s*:\\s*true");

    private static HashSet<int> OwnedPids()
    {
        var pids = new HashSet<int> { Environment.ProcessId };
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.StartsWith("PaRuX", StringComparison.OrdinalIgnoreCase)) pids.Add(process.Id);
                }
                catch (Exception) { /* exited */ }
            }
        }
        return pids;
    }

    private static string ProcessName(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return process.ProcessName; }
        catch (Exception) { return string.Empty; }
    }
}
