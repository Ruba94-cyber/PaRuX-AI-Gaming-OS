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
    private readonly Button _refresh = new() { MinHeight = 44 };
    private readonly Button _resumeAll = new() { MinHeight = 44, Visibility = Visibility.Collapsed };
    private bool _busy;

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
        AutomationProperties.SetAutomationId(_status, "QuickResume_Status_Text");
        AutomationProperties.SetAutomationId(_list, "QuickResume_List");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_resumeAll);
        Children.Add(_heading);
        Children.Add(_description);
        Children.Add(buttons);
        Children.Add(_status);
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
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private async Task ActAsync(QuickResumeAppDto app)
    {
        if (_busy) return;
        var result = await GamingLibraryClient.Current.QuickResumeActionAsync(
            app.Suspended ? "resume" : "suspend", new QuickResumeActionRequest(app.ProcessId, app.StartedUtc));
        _status.Text = result.Value?.Message ?? result.Error ?? T("The action failed.", "Azione non riuscita.");
        await RefreshAsync();
    }

    private async Task ResumeAllAsync()
    {
        var result = await GamingLibraryClient.Current.ResumeAllPausedAsync();
        _status.Text = result.Value?.Message ?? result.Error ?? T("The action failed.", "Azione non riuscita.");
        await RefreshAsync();
    }
}
