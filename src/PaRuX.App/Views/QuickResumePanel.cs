using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views;

/// <summary>
/// Quick Resume: pause any open application so it uses no CPU, and bring it back exactly where it was (the first-party
/// replacement for Nyrna). Lists open application windows with Pause, and what is paused with Resume.
/// Administrator-level applications are listed as unavailable with the reason instead of failing silently.
/// </summary>
internal sealed class QuickResumePanel : StackPanel
{
    private static string T(string en, string it) => GameUi.T(en, it);

    private readonly TextBlock _heading = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _recovery = new() { Spacing = 4 };
    private readonly Button _refresh = new() { MinHeight = 44 };
    private readonly Button _resumeAll = new() { MinHeight = 44, Visibility = Visibility.Collapsed };
    private readonly Button _modRestore = new() { MinHeight = 44 };
    private bool _busy;
    private bool _acting;

    public QuickResumePanel()
    {
        Spacing = 8;
        Margin = new Thickness(0, 16, 0, 0);
        _heading.Text = "Quick Resume";
        AutomationProperties.SetAutomationId(_heading, "QuickResume_Heading");
        AutomationProperties.SetHeadingLevel(_heading, AutomationHeadingLevel.Level3);
        _description.Text = T(
            "Pause an open app or game so it stops using the CPU, then resume it exactly where you left it. Windows and security programs cannot be paused.",
            "Metti in pausa un'app o un gioco aperto, così non usa più la CPU, e riprendilo esattamente da dove l'avevi lasciato. Windows e i programmi di sicurezza non si possono mettere in pausa.");
        _refresh.Content = T("Refresh", "Aggiorna");
        AutomationProperties.SetAutomationId(_refresh, "QuickResume_Refresh_Button");
        AutomationProperties.SetName(_refresh, T("Refresh the list of open apps", "Aggiorna l'elenco delle app aperte"));
        _refresh.Click += async (_, _) => await RefreshAsync();
        _resumeAll.Content = T("Resume all", "Riprendi tutto");
        AutomationProperties.SetAutomationId(_resumeAll, "QuickResume_ResumeAll_Button");
        _resumeAll.Click += async (_, _) => await ResumeAllAsync();
        _modRestore.Content = T("Restore AI mods", "Ripristina mod IA");
        AutomationProperties.SetAutomationId(_modRestore, "QuickResume_Mods_RestoreButton");
        _modRestore.Click += async (_, _) => await TemporaryGameModsDialog.ShowRestoreListAsync(XamlRoot);
        AutomationProperties.SetAutomationId(_status, "QuickResume_Status_Text");
        AutomationProperties.SetAutomationId(_list, "QuickResume_List");
        AutomationProperties.SetAutomationId(_recovery, "QuickResume_Recovery_List");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_resumeAll);
        buttons.Children.Add(_modRestore);
        Children.Add(_heading);
        Children.Add(_description);
        Children.Add(buttons);
        Children.Add(_status);
        Children.Add(_recovery);
        Children.Add(_list);
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var result = await GamingLibraryClient.Current.GetQuickResumeAsync();
            _list.Children.Clear();
            if (!result.Succeeded || result.Value is null)
            {
                _status.Text = T("The app list is unavailable right now.", "L'elenco delle app non è disponibile in questo momento.");
                return;
            }
            ShowRecovery(result.Value);
            var apps = result.Value.Apps;
            int paused = apps.Count(a => a.Suspended);
            _resumeAll.Visibility = paused > 0 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = apps.Count == 0
                ? T("No open apps to pause.", "Nessuna app aperta da mettere in pausa.")
                : paused > 0
                    ? T(paused + " paused.", paused + " in pausa.")
                    : T(apps.Count + " open apps.", apps.Count + " app aperte.");
            foreach (QuickResumeAppDto app in apps) _list.Children.Add(BuildRow(app));
        }
        finally { _busy = false; }
    }

    /// <summary>What the automatic recovery at Core start-up / shutdown did with apps that were still paused. Failures stay visible until acted on.</summary>
    private void ShowRecovery(QuickResumeStatusDto status)
    {
        _recovery.Children.Clear();
        if (status.Recovery is not { Count: > 0 } entries) return;
        _recovery.Children.Add(new TextBlock
        {
            Text = T("Automatic recovery after the last start or shutdown:", "Ripristino automatico dopo l'ultimo avvio o arresto:"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        foreach (QuickResumeRecoveryDto entry in entries.Take(10))
        {
            string outcome = entry.Outcome switch
            {
                "resumed" => T("resumed", "ripreso"),
                "already-closed" => T("already closed", "già chiuso"),
                _ => T("NOT resumed", "NON ripreso"),
            };
            var line = new TextBlock { Text = $"{entry.ProcessName} - {outcome}. {entry.Message}", TextWrapping = TextWrapping.Wrap, Opacity = entry.Outcome == "failed" ? 1 : 0.8 };
            AutomationProperties.SetAutomationId(line, "QuickResume_Recovery_" + entry.ProcessId);
            _recovery.Children.Add(line);
        }
    }

    private UIElement BuildRow(QuickResumeAppDto app)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string title = string.IsNullOrWhiteSpace(app.WindowTitle) ? app.ProcessName : app.WindowTitle;
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title.Length > 90 ? title[..90] + "…" : title,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        string detail = app.ProcessName + (app.Suspended ? " · " + T("Paused", "In pausa") : string.Empty)
            + (!app.CanSuspend && app.Reason is not null && !app.Suspended ? " · " + app.Reason : string.Empty);
        text.Children.Add(new TextBlock { Text = detail, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);

        var button = new Button
        {
            MinHeight = 44,
            MinWidth = 120,
            Content = app.Suspended ? T("Resume", "Riprendi") : T("Pause", "Pausa"),
            IsEnabled = app.Suspended || app.CanSuspend
        };
        AutomationProperties.SetAutomationId(button, (app.Suspended ? "QuickResume_Resume_" : "QuickResume_Pause_") + app.ProcessId);
        AutomationProperties.SetName(button, (app.Suspended ? T("Resume ", "Riprendi ") : T("Pause ", "Metti in pausa ")) + title);
        button.Click += async (_, _) => await ActAsync(app);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(button);
        if (app.Suspended)
        {
            var mods = new Button
            {
                Content = T("AI Mod", "Mod IA"),
                MinHeight = 44, MinWidth = 96
            };
            AutomationProperties.SetAutomationId(mods, "QuickResume_Mods_Open_" + app.ProcessId);
            mods.Click += async (_, _) =>
            {
                mods.IsEnabled = false;
                try { await TemporaryGameModsDialog.ShowAsync(XamlRoot, app); }
                finally { mods.IsEnabled = true; }
            };
            actions.Children.Add(mods);
        }
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private async Task ActAsync(QuickResumeAppDto app)
    {
        if (_busy || _acting) return;
        _acting = true; // a double click must not send two pauses: each one would need its own resume
        try
        {
            var result = await GamingLibraryClient.Current.QuickResumeActionAsync(
                app.Suspended ? "resume" : "suspend", new QuickResumeActionRequest(app.ProcessId, app.StartedUtc));
            string message = result.Value?.Message ?? result.Error ?? T("The action failed.", "Azione non riuscita.");
            await RefreshAsync();
            _status.Text = message; // after the refresh, so the reason a pause failed stays visible
        }
        finally { _acting = false; }
    }

    private async Task ResumeAllAsync()
    {
        if (_busy || _acting) return;
        _acting = true;
        try
        {
            var result = await GamingLibraryClient.Current.ResumeAllPausedAsync();
            string message = result.Value?.Message ?? result.Error ?? T("The action failed.", "Azione non riuscita.");
            await RefreshAsync();
            _status.Text = message;
        }
        finally { _acting = false; }
    }
}
