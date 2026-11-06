using System;
using System.Collections.Generic;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>Corporate Lite stays offline unless explicitly launched for model downloads.
/// Tests use synthetic isolated data; they never pick files or contact Hugging Face.</summary>
[Collection(UiTestCollection.Name)]
public sealed class CorporateModelHubCoverageTests
{
    private static AutomationElement? Find(UiTestApplication app, string id) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static bool Visible(UiTestApplication app, string id)
    {
        AutomationElement? element = Find(app, id);
        return element != null && !element.Properties.IsOffscreen.ValueOrDefault
            && element.BoundingRectangle.Width > 1 && element.BoundingRectangle.Height > 1;
    }

    private static void OpenCorporateModelHubViaSettings(UiTestApplication app)
    {
        // In Corporate the Model Hub intentionally lives behind model management,
        // not in the Chat-first sidebar.
        var nav = new WorkspaceNavigationPage(app.MainWindow, app);
        nav.NavigateTo(WorkspaceNavigationPage.SettingsDescriptor.Id);
        nav.WaitForWorkspace(WorkspaceNavigationPage.SettingsDescriptor);
        UiWait.ById(app.MainWindow, "Settings_Category_Models_Button", ControlType.Button).Click(moveMouse: false);
        UiWait.ById(app.MainWindow, "Settings_Corporate_OpenModelHub_Button", ControlType.Button).Click(moveMouse: false);
        UiWait.ById(app.MainWindow, "ModelHub_Corporate_ImportGguf_Button", ControlType.Button);
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Corporate"), Trait("Category", "FlaUI")]
    public void Offline_corporate_offers_local_import_but_not_online_download_or_media()
    {
        const string test = nameof(Offline_corporate_offers_local_import_but_not_online_download_or_media);
        using var app = new UiTestApplication(test, extraEnvironment: new Dictionary<string, string>
        {
            ["PARUX_CORPORATE"] = "1",
            ["PARUX_CORPORATE_MODEL_DOWNLOADS"] = "0"
        });
        try
        {
            OpenCorporateModelHubViaSettings(app);

            UiWait.ById(app.MainWindow, "ModelHub_Corporate_ImportGguf_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "ModelHub_Corporate_ImportExternal_Button", ControlType.Button);
            Assert.False(Visible(app, "ModelHub_Corporate_BrowseDownloads_Button"));
            Assert.False(Visible(app, "ModelHub_Tab_Media"));
            var status = UiWait.ById(app.MainWindow, "ModelHub_Corporate_ModelNetwork_Status", ControlType.Text);
            Assert.Contains("offline", status.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { app.CaptureFailure(test, ex); throw; }
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "Corporate"), Trait("Category", "FlaUI")]
    public void Approved_corporate_session_enables_model_browse_only()
    {
        const string test = nameof(Approved_corporate_session_enables_model_browse_only);
        using var app = new UiTestApplication(test, extraEnvironment: new Dictionary<string, string>
        {
            ["PARUX_CORPORATE"] = "1",
            ["PARUX_CORPORATE_MODEL_DOWNLOADS"] = "1"
        });
        try
        {
            OpenCorporateModelHubViaSettings(app);

            UiWait.ById(app.MainWindow, "ModelHub_Corporate_ImportGguf_Button", ControlType.Button);
            UiWait.ById(app.MainWindow, "ModelHub_Corporate_BrowseDownloads_Button", ControlType.Button);
            Assert.False(Visible(app, "ModelHub_Tab_Media"));
            var status = UiWait.ById(app.MainWindow, "ModelHub_Corporate_ModelNetwork_Status", ControlType.Text);
            Assert.Contains("download", status.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { app.CaptureFailure(test, ex); throw; }
    }
}
