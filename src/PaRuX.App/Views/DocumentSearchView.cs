using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Document search: the folders the person chose, their index state (progress, pause, cancel) and a search box that works
    /// with no language model loaded. Results carry page / sheet / slide citations and open the original file. Documents are
    /// only read. Reuses the existing RAG folder list; the index itself lives in the user's data root.
    /// </summary>
    public sealed class DocumentSearchView : UserControl
    {
        private static bool It => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
        private static string T(string en, string it) => It ? it : en;

        private readonly StackPanel _folders = new StackPanel { Spacing = 8 };
        private readonly TextBox _query = new TextBox { MinWidth = 320 };
        private readonly ComboBox _mode = new ComboBox { MinWidth = 160 };
        private readonly StackPanel _results = new StackPanel { Spacing = 10 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private List<RagSourceDto> _sources = new List<RagSourceDto>();

        public DocumentSearchView()
        {
            var root = new StackPanel { Spacing = 16, Margin = new Thickness(24), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Center };
            var title = new TextBlock { Text = T("Document search", "Ricerca documenti"), FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
            root.Children.Add(title);
            root.Children.Add(new TextBlock
            {
                Text = T("Search the folders you choose. Everything stays on this PC; documents are only read, never changed. No AI model is needed to search.",
                    "Cerca nelle cartelle che scegli. Tutto resta su questo PC; i documenti vengono solo letti, mai modificati. Per cercare non serve nessun modello AI."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.82
            });

            root.Children.Add(new TextBlock { Text = T("FOLDERS", "CARTELLE"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72 });
            root.Children.Add(_folders);
            var add = new Button { Content = T("Add folder…", "Aggiungi cartella…") };
            AutomationProperties.SetAutomationId(add, "DocSearch_AddFolder_Button");
            add.Click += async (_, _) => await AddFolderAsync();
            root.Children.Add(add);

            root.Children.Add(new TextBlock { Text = T("SEARCH", "RICERCA"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72, Margin = new Thickness(0, 8, 0, 0) });
            _query.PlaceholderText = T("Words, a file name, or an exact code (e.g. ABC_1234)", "Parole, nome di un file o un codice esatto (es. ABC_1234)");
            AutomationProperties.SetAutomationId(_query, "DocSearch_Query_TextBox");
            AutomationProperties.SetName(_query, T("Search documents", "Cerca nei documenti"));
            _query.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await SearchAsync(); };
            foreach ((string tag, string en, string it) in new[] { ("Auto", "Automatic", "Automatica"), ("Exact", "Exact", "Esatta"), ("Lexical", "Words", "Parole"), ("Hybrid", "Meaning + words", "Significato + parole") })
                _mode.Items.Add(new ComboBoxItem { Tag = tag, Content = T(en, it) });
            _mode.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_mode, "DocSearch_Mode_ComboBox");
            AutomationProperties.SetName(_mode, T("Search mode", "Modalità di ricerca"));
            var go = new Button { Content = T("Search", "Cerca"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            AutomationProperties.SetAutomationId(go, "DocSearch_Search_Button");
            go.Click += async (_, _) => await SearchAsync();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_query); row.Children.Add(_mode); row.Children.Add(go);
            root.Children.Add(row);
            AutomationProperties.SetAutomationId(_status, "DocSearch_Status_Text");
            root.Children.Add(_status);
            var chatHint = new TextBlock
            {
                Text = T("To ask questions about these documents with cited answers, use Chat and switch on the Documents button next to the message box.",
                    "Per fare domande su questi documenti con risposte citate, usa la Chat e attiva il pulsante Documenti accanto alla casella del messaggio."),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12
            };
            AutomationProperties.SetAutomationId(chatHint, "DocSearch_ChatHint_Text");
            root.Children.Add(chatHint);
            root.Children.Add(_results);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _poll.Tick += async (_, _) => await RefreshFoldersAsync(false);
            Unloaded += (_, _) => _poll.Stop();
        }

        public async void OnShown() => await RefreshFoldersAsync(true);

        private async Task RefreshFoldersAsync(bool reload)
        {
            try
            {
                if (reload) _sources = await RagClient.Current.ListSourcesAsync(CancellationToken.None);
                _folders.Children.Clear();
                bool running = false;
                foreach (RagSourceDto source in _sources)
                {
                    KnowledgeSourceStatus st = await KnowledgeClient.Current.StatusAsync(source.Id, CancellationToken.None);
                    running |= st.State is "running" or "paused";
                    _folders.Children.Add(FolderRow(source, st));
                }
                if (_sources.Count == 0)
                    _folders.Children.Add(new TextBlock { Text = T("No folder yet. Add one to start.", "Nessuna cartella. Aggiungine una per iniziare."), Opacity = 0.75 });
                if (running) _poll.Start(); else _poll.Stop();
            }
            catch (Exception ex) { _status.Text = T("Could not reach the PaRuX Core: ", "Impossibile raggiungere il Core PaRuX: ") + ex.Message; }
        }

        private UIElement FolderRow(RagSourceDto source, KnowledgeSourceStatus st)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = source.FolderPath, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            string state = st.State switch
            {
                "running" => T("Indexing… ", "Indicizzazione… ") + (st.Current ?? string.Empty),
                "paused" => T("Paused", "In pausa"),
                "done" => T("Up to date", "Aggiornata"),
                "cancelled" => T("Stopped", "Interrotta"),
                "failed" => T("Failed", "Non riuscita"),
                _ => T("Not indexed yet", "Non ancora indicizzata")
            };
            panel.Children.Add(new TextBlock
            {
                Text = state + " · " + st.Files + T(" documents", " documenti") + " · " + st.Chunks + T(" passages", " passaggi")
                    + (st.Problems > 0 ? " · " + st.Problems + T(" need attention", " da controllare") : string.Empty),
                FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            string id = source.Id;
            Button B(string text, string autoId, Func<Task> act)
            {
                var b = new Button { Content = text };
                AutomationProperties.SetAutomationId(b, autoId + "_" + id);
                b.Click += async (_, _) => { try { await act(); } catch (Exception ex) { _status.Text = ex.Message; } await RefreshFoldersAsync(false); };
                return b;
            }
            if (st.State == "running") { buttons.Children.Add(B(T("Pause", "Pausa"), "DocSearch_Pause", () => KnowledgeClient.Current.PauseAsync(id, CancellationToken.None))); buttons.Children.Add(B(T("Stop", "Ferma"), "DocSearch_Cancel", () => KnowledgeClient.Current.CancelAsync(id, CancellationToken.None))); }
            else if (st.State == "paused") buttons.Children.Add(B(T("Resume", "Riprendi"), "DocSearch_Resume", () => KnowledgeClient.Current.ResumeAsync(id, CancellationToken.None)));
            else buttons.Children.Add(B(st.Files == 0 ? T("Index now", "Indicizza ora") : T("Update", "Aggiorna"), "DocSearch_Index", () => KnowledgeClient.Current.StartAsync(id, CancellationToken.None)));
            buttons.Children.Add(B(T("Remove", "Rimuovi"), "DocSearch_Remove", async () => { await RagClient.Current.RemoveSourceAsync(id, CancellationToken.None); _sources = await RagClient.Current.ListSourcesAsync(CancellationToken.None); }));
            panel.Children.Add(buttons);
            return new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel };
        }

        private async Task AddFolderAsync()
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                RagSourceDto source;
                try { source = await RagClient.Current.AddSourceAsync(folder.Path, CancellationToken.None); }
                catch (SensitiveFolderException blocked) when (blocked.Code == "sensitive_blocked")
                {
                    _status.Text = T("This folder is not indexed: ", "Questa cartella non viene indicizzata: ") + blocked.Message;
                    return;
                }
                catch (SensitiveFolderException consent) when (consent.Code == "consent_required")
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, Title = T("Index this broad location?", "Indicizzare questa posizione ampia?"),
                        Content = new TextBlock { Text = consent.Message + "\n\n" + folder.Path, TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = T("Index it", "Indicizza"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close
                    };
                    AutomationProperties.SetAutomationId(dialog, "DocSearch_Consent_Dialog");
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    source = await RagClient.Current.AddSourceAsync(folder.Path, CancellationToken.None, consentSensitive: true);
                }
                await KnowledgeClient.Current.StartAsync(source.Id, CancellationToken.None);
                await RefreshFoldersAsync(true);
            }
            catch (Exception ex) { _status.Text = T("Could not add the folder: ", "Impossibile aggiungere la cartella: ") + ex.Message; }
        }

        private async Task SearchAsync()
        {
            string q = _query.Text.Trim();
            if (q.Length == 0) return;
            _results.Children.Clear();
            _status.Text = T("Searching…", "Ricerca…");
            try
            {
                string mode = (_mode.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
                List<KnowledgeHitView> hits = await KnowledgeClient.Current.SearchAsync(q, mode, 15, CancellationToken.None);
                _status.Text = hits.Count == 0
                    ? T("Nothing found in the indexed folders. Try other words, or check that the folder is indexed.", "Nessun risultato nelle cartelle indicizzate. Prova altre parole o controlla che la cartella sia indicizzata.")
                    : hits.Count + T(" results", " risultati");
                foreach (KnowledgeHitView hit in hits)
                {
                    var item = new StackPanel { Spacing = 3 };
                    item.Children.Add(new TextBlock { Text = hit.Citation, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    string snippet = hit.Text.Length > 320 ? hit.Text[..320] + "…" : hit.Text;
                    item.Children.Add(new TextBlock { Text = snippet, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 13 });
                    string where = DocumentOpenPlanner.Describe(hit.Page, hit.Sheet, hit.CellRange, hit.Slide, hit.Heading);
                    var open = new Button
                    {
                        Content = where.Length == 0 ? T("Open file", "Apri file") : T("Open at ", "Apri a ") + where,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    AutomationProperties.SetName(open, T("Open ", "Apri ") + hit.FileName + (where.Length == 0 ? string.Empty : " " + where));
                    AutomationProperties.SetAutomationId(open, "DocSearch_Open_Button");
                    KnowledgeHitView captured = hit;
                    open.Click += async (_, _) => _status.Text = await DocumentOpener.OpenAsync(captured.FilePath, captured.Page, captured.Sheet, captured.CellRange, captured.Slide, captured.Heading);
                    item.Children.Add(open);
                    _results.Children.Add(new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = item });
                }
            }
            catch (Exception ex) { _status.Text = T("Search failed: ", "Ricerca non riuscita: ") + ex.Message; }
        }
    }
}
