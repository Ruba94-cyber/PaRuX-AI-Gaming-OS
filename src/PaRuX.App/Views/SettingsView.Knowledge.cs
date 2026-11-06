using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Document index status and options inside the existing RAG card: per-folder progress, pause/resume, the list of files that
    /// need attention, OCR opt-in, the resource profile and strict-offline. No new page; the Core owns the state.
    /// </summary>
    public sealed partial class SettingsView
    {
        private sealed class KnowledgeRowUi
        {
            public TextBlock Status = null!;
            public ProgressBar Progress = null!;
            public Button PauseResume = null!;
            public Expander Problems = null!;
            public StackPanel ProblemList = null!;
            public string State = "idle";
        }

        private readonly Dictionary<string, KnowledgeRowUi> _knowledgeRows = new Dictionary<string, KnowledgeRowUi>();
        private DispatcherTimer? _knowledgePoll;
        private bool _knowledgeSettingsBuilt;
        private bool _knowledgeSettingsLoading;

        private static string KT(string en, string it) =>
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        private void AddKnowledgeStatusRows(StackPanel host, PaRuX.Contracts.RagSourceDto source)
        {
            string id = source.Id;
            var ui = new KnowledgeRowUi();
            ui.Status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyCaptionStyle(ui.Status);
            AutomationProperties.SetAutomationId(ui.Status, "Settings_Rag_Status_" + id);
            ui.Progress = new ProgressBar { IsIndeterminate = false, Value = 0, Maximum = 1, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(ui.Progress, "Settings_Rag_Progress_" + id);
            ui.PauseResume = new Button { Content = KT("Pause", "Pausa"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(ui.PauseResume, "Settings_Rag_PauseResume_" + id);
            ui.PauseResume.Click += async (s, e) =>
            {
                try
                {
                    if (ui.State == "paused") await KnowledgeClient.Current.ResumeAsync(id, CancellationToken.None);
                    else await KnowledgeClient.Current.PauseAsync(id, CancellationToken.None);
                }
                catch (Exception ex) { ui.Status.Text = ex.Message; }
                await UpdateKnowledgeRowAsync(id, ui);
            };
            ui.ProblemList = new StackPanel { Spacing = 2 };
            AutomationProperties.SetAutomationId(ui.ProblemList, "Settings_Rag_ProblemsList_" + id);
            ui.Problems = new Expander
            {
                Header = KT("Files that need attention", "File da controllare"), Content = ui.ProblemList, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(ui.Problems, "Settings_Rag_Problems_" + id);
            host.Children.Add(ui.Status);
            host.Children.Add(ui.Progress);
            host.Children.Add(ui.PauseResume);
            host.Children.Add(ui.Problems);
            _knowledgeRows[id] = ui;
            _ = UpdateKnowledgeRowAsync(id, ui);
        }

        private async Task UpdateKnowledgeRowAsync(string id, KnowledgeRowUi ui)
        {
            try
            {
                KnowledgeFullStatus st = await KnowledgeClient.Current.FullStatusAsync(id, CancellationToken.None);
                ui.State = st.State;
                bool running = st.State is "running" or "paused";
                string stateText = st.State switch
                {
                    "running" => KT("Indexing", "Indicizzazione"),
                    "paused" => KT("Paused", "In pausa"),
                    "done" => KT("Up to date", "Aggiornata"),
                    "cancelled" => KT("Stopped", "Interrotta"),
                    "failed" => st.Error == "blocked_sensitive_path" ? KT("Blocked: credential or private folder", "Bloccata: cartella con credenziali o dati privati") : KT("Failed", "Non riuscita"),
                    _ => KT("Not indexed by the document index yet", "Non ancora indicizzata")
                };
                string counts = st.Progress == null ? string.Empty
                    : " · " + st.Progress.FilesSeen + KT(" files seen", " file esaminati") + ", " + st.Progress.Indexed + KT(" indexed", " indicizzati")
                      + ", " + st.Progress.Unchanged + KT(" unchanged", " invariati") + ", " + st.Progress.MetadataOnly + KT(" by name only", " solo per nome")
                      + ", " + st.Progress.Failed + KT(" failed", " falliti") + ", " + st.Progress.Skipped + KT(" skipped", " saltati");
                string location = st.Progress?.CurrentPage is int page && st.Progress.TotalPages is int pages
                    ? " · " + KT("PDF page ", "PDF pagina ") + page.ToString("N0")
                      + "/" + pages.ToString("N0")
                    : st.Progress?.Current is { Length: > 0 } current
                        ? " · " + current : string.Empty;
                ui.Status.Text = stateText + " · " + st.Files + KT(" documents", " documenti")
                    + ", " + st.Chunks + KT(" passages", " passaggi") + counts + location;
                ui.Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                // Only claim a real percentage when the PDF parser supplies
                // concrete page/total counts. Folder traversal has no total.
                bool pageProgress = running && st.Progress?.CurrentPage is > 0
                    && st.Progress?.TotalPages is > 0;
                ui.Progress.IsIndeterminate = running && !pageProgress;
                if (pageProgress)
                {
                    ui.Progress.Maximum = st.Progress!.TotalPages!.Value;
                    ui.Progress.Value = st.Progress.CurrentPage!.Value;
                }
                ui.PauseResume.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                ui.PauseResume.Content = st.State == "paused" ? KT("Resume", "Riprendi") : KT("Pause", "Pausa");

                ui.ProblemList.Children.Clear();
                foreach (KnowledgeProblem problem in st.Problems.Take(200))
                {
                    var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = problem.Kind + " · " + problem.RelativePath + " — " + problem.Reason };
                    ApplyCaptionStyle(line);
                    ui.ProblemList.Children.Add(line);
                }
                ui.Problems.Visibility = st.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                ui.Problems.Header = KT("Files that need attention", "File da controllare") + " (" + st.Problems.Count + ")";
                if (running) EnsureKnowledgePoll();
            }
            catch (Exception ex) { Log.Write("UpdateKnowledgeRowAsync failed: " + ex.Message); }
        }

        private void EnsureKnowledgePoll()
        {
            if (_knowledgePoll == null)
            {
                _knowledgePoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _knowledgePoll.Tick += async (s, e) =>
                {
                    bool any = false;
                    foreach (KeyValuePair<string, KnowledgeRowUi> row in _knowledgeRows.ToList())
                    {
                        await UpdateKnowledgeRowAsync(row.Key, row.Value);
                        any |= row.Value.State is "running" or "paused";
                    }
                    if (!any) _knowledgePoll?.Stop();
                };
                Unloaded += (s, e) => _knowledgePoll?.Stop();
            }
            if (!_knowledgePoll.IsEnabled) _knowledgePoll.Start();
        }

        /// <summary>OCR opt-in, resource profile and strict offline. Built once; values come from the Core.</summary>
        private async Task EnsureKnowledgeSettingsPanelAsync()
        {
            if (KnowledgeSettingsPanel == null || _knowledgeSettingsBuilt) return;
            _knowledgeSettingsBuilt = true;
            _knowledgeSettingsLoading = true;
            try
            {
                KnowledgeSettingsView s = await KnowledgeClient.Current.GetSettingsAsync(probeOcr: true, CancellationToken.None);

                var ocr = new ToggleSwitch { Header = KT("Read text in images and scanned PDFs (OCR)", "Leggi il testo in immagini e PDF scansionati (OCR)"), IsOn = s.OcrEnabled };
                AutomationProperties.SetAutomationId(ocr, "Settings_Rag_Ocr_Switch");
                var ocrNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(ocrNote);
                AutomationProperties.SetAutomationId(ocrNote, "Settings_Rag_Ocr_Note");
                ocrNote.Text = s.OcrAvailable == true
                    ? KT("Off by default. Uses Windows OCR on this PC (languages: ", "Disattivato di default. Usa l'OCR di Windows su questo PC (lingue: ") + s.OcrLanguages + "). " + KT("Nothing leaves the PC.", "Nulla esce dal PC.")
                    : KT("Off by default. Windows OCR is not available here: ", "Disattivato di default. L'OCR di Windows non è disponibile qui: ") + (s.OcrReason ?? "?")
                      + KT(" Images and scanned PDFs stay searchable by name only.", " Immagini e PDF scansionati restano ricercabili solo per nome.");
                ocr.Toggled += async (a, b) =>
                {
                    if (_knowledgeSettingsLoading) return;
                    try { await KnowledgeClient.Current.SaveSettingsAsync(ocr.IsOn, null, null, CancellationToken.None); }
                    catch (Exception ex) { ocrNote.Text = ex.Message; }
                };

                var profile = new ComboBox { Header = KT("Indexing effort", "Impegno dell'indicizzazione"), MinWidth = 260 };
                foreach ((string tag, string label) in new[]
                {
                    ("auto", KT("Automatic (recommended: ", "Automatico (consigliato: ") + s.RecommendedProfile + ")"),
                    ("Eco", KT("Eco - light, slow, for 16 GB PCs", "Eco - leggero, lento, per PC da 16 GB")),
                    ("Balanced", KT("Balanced", "Bilanciato")),
                    ("Performance", KT("Performance - fastest", "Prestazioni - più veloce"))
                })
                    profile.Items.Add(new ComboBoxItem { Tag = tag, Content = label });
                profile.SelectedIndex = s.ProfileIsAutomatic ? 0 : Math.Max(0, new[] { "auto", "Eco", "Balanced", "Performance" }.ToList().IndexOf(s.Profile));
                AutomationProperties.SetAutomationId(profile, "Settings_Rag_Profile_ComboBox");
                var profileNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(profileNote);
                profileNote.Text = KT("Current plan: ", "Piano attuale: ") + s.Profile + " · " + s.EmbeddingThreads + KT(" thread(s), memory budget ", " thread, budget memoria ")
                    + s.MemoryBudgetMb + " MB · " + KT("indexing runs at low priority and unloads the search model when idle.", "l'indicizzazione gira a bassa priorità e scarica il modello di ricerca quando è inattivo.");
                profile.SelectionChanged += async (a, b) =>
                {
                    if (_knowledgeSettingsLoading || profile.SelectedItem is not ComboBoxItem item) return;
                    try { await KnowledgeClient.Current.SaveSettingsAsync(null, (string)item.Tag, null, CancellationToken.None); }
                    catch (Exception ex) { profileNote.Text = ex.Message; }
                };

                var offline = new ToggleSwitch
                {
                    Header = KT("Strict offline (block model downloads)", "Solo offline (blocca i download dei modelli)"),
                    IsOn = s.StrictOffline, IsEnabled = !s.CorporateOffline
                };
                AutomationProperties.SetAutomationId(offline, "Settings_Rag_StrictOffline_Switch");
                var offlineNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(offlineNote);
                offlineNote.Text = s.CorporateOffline
                    ? KT("Always on in the Corporate edition.", "Sempre attivo nell'edizione Corporate.")
                    : KT("When on, the semantic-search and reranker models cannot be downloaded. Downloads are checked against a pinned SHA-256 before use.",
                        "Se attivo, i modelli di ricerca semantica e di riordino non possono essere scaricati. I download sono verificati con SHA-256 prima dell'uso.");
                offline.Toggled += async (a, b) =>
                {
                    if (_knowledgeSettingsLoading) return;
                    try { await KnowledgeClient.Current.SaveSettingsAsync(null, null, offline.IsOn, CancellationToken.None); }
                    catch (Exception ex) { offlineNote.Text = ex.Message; }
                };

                var wipe = new Button { Content = KT("Delete everything (secure)", "Elimina tutto (sicuro)") };
                AutomationProperties.SetAutomationId(wipe, "Settings_Rag_DeleteEverything_Button");
                var wipeNote = new TextBlock { TextWrapping = TextWrapping.Wrap };
                ApplyCaptionStyle(wipeNote);
                AutomationProperties.SetAutomationId(wipeNote, "Settings_Rag_DeleteEverything_Note");
                wipeNote.Text = KT("Removes every indexed folder and wipes the search index (text, word and partial-match entries, vectors, database files). Your documents are not touched.",
                    "Rimuove ogni cartella indicizzata e cancella l'indice di ricerca (testo, voci per parole e corrispondenze parziali, vettori, file del database). I tuoi documenti non vengono toccati.");
                wipe.Click += async (a, b) =>
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, Title = KT("Delete the whole document index?", "Eliminare tutto l'indice dei documenti?"),
                        Content = new TextBlock { Text = wipeNote.Text, TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = KT("Delete everything", "Elimina tutto"), CloseButtonText = KT("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close
                    };
                    AutomationProperties.SetAutomationId(dialog, "Settings_Rag_DeleteEverything_Dialog");
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    try
                    {
                        wipe.IsEnabled = false;
                        (int files, int chunks, bool onDisk) = await KnowledgeClient.Current.DeleteEverythingAsync(CancellationToken.None);
                        wipeNote.Text = KT("Deleted ", "Eliminati ") + files + KT(" files / ", " file / ") + chunks + KT(" passages from the index. ", " passaggi dall'indice. ")
                            + (onDisk ? KT("Database files were zeroed and removed.", "I file del database sono stati azzerati e rimossi.") : KT("Some database files could not be removed (in use).", "Alcuni file del database non sono stati rimossi (in uso)."));
                        await RefreshRagSourcesAsync();
                    }
                    catch (Exception ex) { wipeNote.Text = KT("Could not delete: ", "Eliminazione non riuscita: ") + ex.Message; }
                    finally { wipe.IsEnabled = true; }
                };

                KnowledgeSettingsPanel.Children.Clear();
                foreach (UIElement e in new UIElement[] { ocr, ocrNote, profile, profileNote, offline, offlineNote, wipe, wipeNote }) KnowledgeSettingsPanel.Children.Add(e);
            }
            catch (Exception ex)
            {
                _knowledgeSettingsBuilt = false;
                Log.Write("EnsureKnowledgeSettingsPanelAsync failed: " + ex.Message);
            }
            finally { _knowledgeSettingsLoading = false; }
        }
    }
}
