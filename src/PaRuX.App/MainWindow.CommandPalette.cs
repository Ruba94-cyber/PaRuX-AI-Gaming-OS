using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PaRuX.Services;
using Windows.System;

namespace PaRuX
{
    /// <summary>One entry of the Ctrl+K palette. Keywords are extra search words (English and Italian) that are not part of the title.</summary>
    internal sealed record PaletteCommand(string Id, string Title, string Keywords, Action Run);

    /// <summary>
    /// Ctrl+K command palette: type a few letters to jump to a workspace, switch AI/Gaming, start a new chat or toggle Console/TV.
    /// It reuses the sidebar's own navigation (same profile gate), so it can never open a surface the sidebar would hide.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _commandPaletteOpen;

        private void InitializeCommandPalette()
        {
            var accelerator = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += async (_, args) =>
            {
                args.Handled = true;
                await ShowCommandPaletteAsync();
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }

        private List<PaletteCommand> BuildPaletteCommands()
        {
            var list = new List<PaletteCommand>();
            void Add(string id, string en, string it, string keywords, Action run) =>
                list.Add(new PaletteCommand(id, ConsoleText(en, it), keywords, run));

            if (CurrentSetupPlan.ShowModeTabs)
            {
                Add("mode-ai", "Switch to AI", "Passa a IA", "mode tab artificial intelligence modalita", () => SelectModeTab(ModeKeyAi, navigate: true));
                Add("mode-gaming", "Switch to Gaming", "Passa a Gaming", "mode tab games giochi modalita libreria", () => SelectModeTab(ModeKeyGaming, navigate: true));
            }
            void Workspace(string tag, string en, string it, string keywords)
            {
                if (!IsWorkspaceAllowedForSetup(tag)) return;
                Add("go-" + tag.Trim('_'), "Go to " + en, "Vai a " + it, keywords, () =>
                {
                    if (tag == "__games__") SelectModeTab(ModeKeyGaming, navigate: true);
                    else if (IsGamingModeTab) SelectModeTab(ModeKeyAi, navigate: false);
                    TryNavigateWorkspace(tag);
                });
            }
            Workspace("__chat__", "Chat", "Chat", "conversation talk message conversazione");
            Workspace("__create__", "Create", "Crea", "image video music speech immagine musica voce 3d");
            Workspace("__projects__", "Projects", "Progetti", "artifacts gallery repository artefatti");
            Workspace("__modelhub__", "Model Hub", "Model Hub", "models download install modelli scarica");
            Workspace("__documents__", "Documents", "Documenti", "search files rag cerca file");
            Workspace("__settings__", "Settings", "Impostazioni", "preferences options language theme lingua tema opzioni");
            Workspace("__help__", "Help", "Aiuto", "guide support how to supporto");
            if (StudioModeToggle.IsOn)
            {
                Workspace("__modelrouting__", "Model routing", "Instradamento dei modelli", "routes roles ruoli");
                Workspace("__runtime__", "Runtime", "Runtime", "engine llama backend motore");
                Workspace("__lifecycle__", "Lifecycle Lab", "Lifecycle Lab", "lab test");
                Workspace("__workflows__", "Workflows", "Workflow", "automation automazione");
                Workspace("__tools__", "Tools", "Strumenti", "mcp skills agents agenti");
                Workspace("__observability__", "Observability", "Osservabilità", "logs metrics log metriche");
                Workspace("__updates__", "Updates", "Aggiornamenti", "update version versione");
            }
            Add("new-chat", "New chat", "Nuova chat", "start conversation clear nuova conversazione", () => NewChat_Click(NewChatButton, new RoutedEventArgs()));
            Add("console", "Toggle Console / TV mode", "Attiva o disattiva Console / TV", "fullscreen tv controller big picture schermo intero",
                () => ConsoleModeButton_Click(ConsoleModeButton, new RoutedEventArgs()));
            return list;
        }

        private async System.Threading.Tasks.Task ShowCommandPaletteAsync()
        {
            if (_commandPaletteOpen || App.IsShuttingDown || Content?.XamlRoot is null) return;
            _commandPaletteOpen = true;
            try
            {
                List<PaletteCommand> all = BuildPaletteCommands();
                var search = new TextBox
                {
                    PlaceholderText = ConsoleText("Type a command or a workspace…", "Scrivi un comando o un'area…"),
                    MinWidth = 380,
                };
                AutomationProperties.SetAutomationId(search, "CommandPalette_Search");
                AutomationProperties.SetName(search, ConsoleText("Search commands", "Cerca comandi"));
                var list = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
                AutomationProperties.SetAutomationId(list, "CommandPalette_List");
                var empty = new TextBlock { Text = ConsoleText("No matching command", "Nessun comando corrispondente"), Opacity = 0.7, Visibility = Visibility.Collapsed, Margin = new Thickness(4) };
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(search);
                panel.Children.Add(list);
                panel.Children.Add(empty);
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = ConsoleText("Command palette", "Tavolozza dei comandi"),
                    Content = panel,
                    CloseButtonText = ConsoleText("Close", "Chiudi"),
                };
                AutomationProperties.SetAutomationId(dialog, "CommandPalette_Dialog");

                PaletteCommand? chosen = null;
                void Refresh()
                {
                    IReadOnlyList<PaletteCommand> shown = CommandPaletteFilter.Filter(all, search.Text, c => c.Title, c => c.Keywords);
                    list.Items.Clear();
                    foreach (PaletteCommand command in shown)
                    {
                        var item = new ListViewItem { Content = command.Title, Tag = command };
                        AutomationProperties.SetAutomationId(item, "CommandPalette_Item_" + command.Id);
                        AutomationProperties.SetName(item, command.Title);
                        list.Items.Add(item);
                    }
                    empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (shown.Count > 0) list.SelectedIndex = 0;
                }
                void Run(PaletteCommand? command)
                {
                    if (command is null) return;
                    chosen = command;
                    dialog.Hide();
                }
                search.TextChanged += (_, _) => Refresh();
                search.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; }
                    else if (e.Key == VirtualKey.Up && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
                    else if (e.Key == VirtualKey.Enter) { Run((list.SelectedItem as ListViewItem)?.Tag as PaletteCommand); e.Handled = true; }
                };
                list.ItemClick += (_, e) => Run((e.ClickedItem as ListViewItem)?.Tag as PaletteCommand);
                dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);
                Refresh();
                await dialog.ShowAsync();
                chosen?.Run();
            }
            finally { _commandPaletteOpen = false; }
        }
    }
}
