using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>What the owner chose in the smart collections dialog.</summary>
    internal sealed record SmartCollectionChoice(bool Changed, GameSmartCollection? Collection);

    /// <summary>
    /// Smart collections: saved filters that fill themselves ("Playing and rated 8+", "Not played for 90 days"), like Playnite's filter
    /// presets. The rules are kept by Core; the match is computed here over the library the page already holds, so counts are live.
    /// </summary>
    internal static class SmartCollectionsDialog
    {
        private static string T(string en, string it) => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        private static readonly (string Field, string En, string It)[] FieldLabels =
        {
            ("playHours", "Hours played", "Ore giocate"), ("score", "Rating (0-10)", "Voto (0-10)"),
            ("daysSincePlayed", "Days since last played", "Giorni dall'ultima partita"), ("daysSinceAdded", "Days since added", "Giorni dall'aggiunta"),
            ("status", "Status", "Stato"), ("tag", "Tag", "Tag"), ("collection", "Collection", "Collezione"), ("name", "Name", "Nome"),
            ("favorite", "Favourite", "Preferito"),
        };

        private static string OpLabel(string op) => op switch
        {
            "gte" => T("at least", "almeno"), "lte" => T("at most", "al massimo"), "eq" => T("is", "è"), "neq" => T("is not", "non è"),
            "contains" => T("contains", "contiene"), _ => op,
        };

        private static string[] OpsFor(string field) => field switch
        {
            "playHours" or "score" or "daysSincePlayed" or "daysSinceAdded" => new[] { "gte", "lte", "eq", "neq" },
            "favorite" => new[] { "eq", "neq" },
            _ => new[] { "contains", "eq", "neq" },
        };

        public static async Task<SmartCollectionChoice> ShowAsync(XamlRoot root, IReadOnlyList<GameTitleDto> titles)
        {
            GameSmartCollection? chosen = null;
            bool changed = false;
            var panel = new StackPanel { Spacing = 10, MinWidth = 460, MaxWidth = 560 };
            var listHost = new StackPanel { Spacing = 6 };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            AutomationProperties.SetAutomationId(status, "SmartCollections_Status_Text");
            var dialog = new ContentDialog
            {
                Title = T("Smart collections", "Collezioni intelligenti"),
                Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
                CloseButtonText = T("Close", "Chiudi"), XamlRoot = root,
            };
            AutomationProperties.SetAutomationId(dialog, "SmartCollections_Dialog");

            async Task RefreshAsync()
            {
                listHost.Children.Clear();
                try
                {
                    List<GameSmartCollection> all = await OwnerRecordsClient.Current.ListSmartCollectionsAsync(CancellationToken.None);
                    if (all.Count == 0) listHost.Children.Add(new TextBlock { Text = T("No smart collections yet.", "Ancora nessuna collezione intelligente."), Opacity = 0.7 });
                    foreach (GameSmartCollection c in all)
                    {
                        int count = titles.Count(t => GameSmartCollectionRules.Matches(t, c, DateTime.UtcNow));
                        var row = new Grid { ColumnSpacing = 6 };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        row.Children.Add(new TextBlock { Text = c.Name + " · " + count + " " + T("games", "giochi"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                        var show = new Button { Content = T("Show", "Mostra") };
                        AutomationProperties.SetAutomationId(show, "SmartCollections_Show_" + c.Id);
                        GameSmartCollection captured = c;
                        show.Click += (_, _) => { chosen = captured; changed = true; dialog.Hide(); };
                        var delete = new Button { Content = T("Delete", "Elimina") };
                        AutomationProperties.SetAutomationId(delete, "SmartCollections_Delete_" + c.Id);
                        delete.Click += async (_, _) =>
                        {
                            try { await OwnerRecordsClient.Current.DeleteSmartCollectionAsync(captured.Id, CancellationToken.None); status.Text = T("Deleted.", "Eliminata."); }
                            catch (Exception ex) { status.Text = ex.Message; }
                            await RefreshAsync();
                        };
                        Grid.SetColumn(show, 1); Grid.SetColumn(delete, 2);
                        row.Children.Add(show); row.Children.Add(delete);
                        listHost.Children.Add(row);
                    }
                }
                catch (Exception ex) { status.Text = T("Could not read your smart collections: ", "Impossibile leggere le collezioni intelligenti: ") + ex.Message; }
            }

            // ---- new collection form ----
            var name = new TextBox { Header = T("Name", "Nome"), PlaceholderText = T("Playing and rated 8+", "In corso e voto 8+"), MaxLength = 40 };
            AutomationProperties.SetAutomationId(name, "SmartCollections_Name_TextBox");
            var matchAll = new ToggleSwitch { Header = T("Match", "Corrispondenza"), OnContent = T("All rules", "Tutte le regole"), OffContent = T("Any rule", "Una qualsiasi regola"), IsOn = true };
            var rulesHost = new StackPanel { Spacing = 6 };
            var ruleRows = new List<(ComboBox Field, ComboBox Op, TextBox Value)>();

            void AddRuleRow()
            {
                if (ruleRows.Count >= GameSmartCollectionRules.MaximumRules) return;
                var field = new ComboBox { MinWidth = 150 };
                foreach (var f in FieldLabels) field.Items.Add(new ComboBoxItem { Content = T(f.En, f.It), Tag = f.Field });
                var op = new ComboBox { MinWidth = 100 };
                var value = new TextBox { MinWidth = 100, PlaceholderText = T("value", "valore") };
                void FillOps()
                {
                    string f = (field.SelectedItem as ComboBoxItem)?.Tag as string ?? "playHours";
                    op.Items.Clear();
                    foreach (string o in OpsFor(f)) op.Items.Add(new ComboBoxItem { Content = OpLabel(o), Tag = o });
                    op.SelectedIndex = 0;
                    value.PlaceholderText = f == "favorite" ? "true / false" : T("value", "valore");
                }
                field.SelectionChanged += (_, _) => FillOps();
                field.SelectedIndex = 0;
                int n = ruleRows.Count;
                AutomationProperties.SetAutomationId(field, "SmartCollections_Rule" + n + "_Field");
                AutomationProperties.SetAutomationId(op, "SmartCollections_Rule" + n + "_Op");
                AutomationProperties.SetAutomationId(value, "SmartCollections_Rule" + n + "_Value");
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(field); row.Children.Add(op); row.Children.Add(value);
                rulesHost.Children.Add(row);
                ruleRows.Add((field, op, value));
            }
            AddRuleRow();

            var addRule = new Button { Content = T("Add a rule", "Aggiungi una regola") };
            addRule.Click += (_, _) => AddRuleRow();
            var save = new Button { Content = T("Save smart collection", "Salva collezione intelligente") };
            AutomationProperties.SetAutomationId(save, "SmartCollections_Save_Button");
            save.Click += async (_, _) =>
            {
                var collection = new GameSmartCollection
                {
                    Name = name.Text, MatchAll = matchAll.IsOn,
                    Rules = ruleRows.Select(r => new GameSmartRule
                    {
                        Field = (r.Field.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
                        Op = (r.Op.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
                        Value = r.Value.Text.Trim(),
                    }).Where(r => r.Value.Length > 0).ToList(),
                };
                try
                {
                    await OwnerRecordsClient.Current.SaveSmartCollectionAsync(collection, CancellationToken.None);
                    status.Text = T("Saved.", "Salvata.");
                    name.Text = string.Empty;
                    await RefreshAsync();
                }
                catch (Exception ex) { status.Text = ex.Message; }
            };

            var clear = new Button { Content = T("Show the whole library again", "Mostra di nuovo tutta la libreria") };
            AutomationProperties.SetAutomationId(clear, "SmartCollections_Clear_Button");
            clear.Click += (_, _) => { chosen = null; changed = true; dialog.Hide(); };

            panel.Children.Add(new TextBlock
            {
                Text = T("A smart collection is a saved filter that updates itself as you play. Hidden games never appear in one.",
                         "Una collezione intelligente è un filtro salvato che si aggiorna mentre giochi. I giochi nascosti non vi compaiono mai."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
            });
            panel.Children.Add(listHost);
            panel.Children.Add(clear);
            panel.Children.Add(new TextBlock { Text = T("NEW", "NUOVA"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(name);
            panel.Children.Add(matchAll);
            panel.Children.Add(rulesHost);
            panel.Children.Add(addRule);
            panel.Children.Add(save);
            panel.Children.Add(status);

            await RefreshAsync();
            await dialog.ShowAsync();
            return new SmartCollectionChoice(changed, chosen);
        }
    }
}
