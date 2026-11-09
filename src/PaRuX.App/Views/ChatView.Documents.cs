using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Composer control for document questions. All AI interaction stays in this Chat: with "Documents" on, the Core answers from the
    /// folders chosen here, cites the file and page/sheet/slide, answers exact lookups without a model, and says "not enough
    /// information" instead of guessing. The Document search page only manages and searches the index.
    /// </summary>
    public sealed partial class ChatView
    {
        private ToggleSwitch? _documentsSwitch;
        private StackPanel? _documentsSources;
        private TextBlock? _documentsNote;
        private TextBox? _documentsSourceFilter;
        private TextBlock? _documentsSourcePageLabel;
        private Button? _documentsPreviousPage;
        private Button? _documentsNextPage;
        private List<RagSourceDto> _documentsAvailableSources = new List<RagSourceDto>();
        private int _documentsPageIndex;
        private int _documentsSourceLoadGeneration;
        private bool _documentsScopeRevoked;

        private static string DT(string en, string it) =>
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? it : en;

        private void InitDocumentsButton()
        {
            if (DocumentsButton == null) return;
            _documentsSwitch = new ToggleSwitch { Header = DT("Answer from my documents", "Rispondi dai miei documenti"), IsOn = ChatDocumentScopeState.Enabled, MinWidth = 280 };
            AutomationProperties.SetAutomationId(_documentsSwitch, "Chat_Documents_Switch");
            _documentsSwitch.Toggled += (s, e) =>
            {
                ChatDocumentScopeState.SetEnabled(_documentsSwitch.IsOn);
                if (_documentsSwitch.IsOn) _documentsScopeRevoked = false;
                UpdateDocumentsButtonVisual();
                UpdateDocumentScopeSummary();
            };
            _documentsNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12, MaxWidth = 320 };
            AutomationProperties.SetAutomationId(_documentsNote, "Chat_Documents_Note");
            _documentsSources = new StackPanel { Spacing = 2 };
            AutomationProperties.SetAutomationId(_documentsSources, "Chat_Documents_Sources");
            var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4) };
            panel.Children.Add(_documentsSwitch);
            panel.Children.Add(new TextBlock
            {
                Text = DT("Select folders to restrict the answer. Turning Documents on with no selection uses all enabled folders.",
                    "Seleziona le cartelle per limitare la risposta. Attivare Documenti senza selezione usa tutte le cartelle attive."),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.8
            });
            _documentsSourceFilter = new TextBox
            {
                PlaceholderText = DT("Find a folder by name or path", "Trova una cartella per nome o percorso"),
                MinWidth = 0, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(_documentsSourceFilter, "Chat_Documents_SourceFilter_TextBox");
            _documentsSourceFilter.TextChanged += (_, _) =>
            {
                _documentsPageIndex = 0;
                RenderDocumentSourcePage();
            };
            panel.Children.Add(_documentsSourceFilter);
            var sourceScroll = new ScrollViewer
            {
                Content = _documentsSources, MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            AutomationProperties.SetAutomationId(sourceScroll, "Chat_Documents_Sources_Scroll");
            panel.Children.Add(sourceScroll);
            var pagination = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _documentsPreviousPage = new Button { Content = DT("Previous", "Precedente"), MinHeight = 40 };
            _documentsNextPage = new Button { Content = DT("Next", "Successiva"), MinHeight = 40 };
            _documentsSourcePageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8 };
            AutomationProperties.SetAutomationId(_documentsPreviousPage, "Chat_Documents_PrevPage_Button");
            AutomationProperties.SetAutomationId(_documentsNextPage, "Chat_Documents_NextPage_Button");
            AutomationProperties.SetAutomationId(_documentsSourcePageLabel, "Chat_Documents_Page_Text");
            _documentsPreviousPage.Click += (_, _) => { _documentsPageIndex--; RenderDocumentSourcePage(); };
            _documentsNextPage.Click += (_, _) => { _documentsPageIndex++; RenderDocumentSourcePage(); };
            pagination.Children.Add(_documentsPreviousPage);
            pagination.Children.Add(_documentsSourcePageLabel);
            pagination.Children.Add(_documentsNextPage);
            panel.Children.Add(pagination);
            panel.Children.Add(_documentsNote);
            panel.Children.Add(new TextBlock { Text = DT("Work with evidence", "Lavora con le fonti"), FontSize = 12, Opacity = 0.8 });
            panel.Children.Add(SkillButton(DT("Summarize with sources", "Riassumi con le fonti"),
                "Chat_Documents_Skill_Summary",
                DT("Summarize only the retrieved passages relevant to this topic. Cite each key point, explain missing coverage, and do not claim to have read every file. Topic: ",
                   "Riassumi solo i passaggi recuperati pertinenti a questo argomento. Cita ogni punto, indica le parti non coperte e non affermare di aver letto tutti i file. Argomento: ")));
            panel.Children.Add(SkillButton(DT("Compare two versions", "Confronta due versioni"), "Chat_Documents_Skill_Diff", "/diff  |  ", caret: "/diff ".Length));
            panel.Children.Add(SkillButton(DT("Check for contradictions", "Cerca contraddizioni"), "Chat_Documents_Skill_Contradictions", "/contradictions "));
            var flyout = new Flyout { Content = panel };
            flyout.Opening += async (s, e) => await LoadDocumentSourcesAsync();
            DocumentsButton.Flyout = flyout;
            UpdateDocumentsButtonVisual();
        }

        /// <summary>Quick action: turns Documents on and puts the command template in the composer for the person to complete and send.</summary>
        private Button SkillButton(string label, string automationId, string template, int? caret = null)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += (s, e) =>
            {
                // Never discard a draft merely because the user chose a shortcut.
                if (InputBox != null && !string.IsNullOrWhiteSpace(InputBox.Text))
                {
                    if (_documentsNote != null)
                        _documentsNote.Text = DT("Send or clear your current draft before inserting this action.",
                            "Invia o cancella la bozza attuale prima di inserire questa azione.");
                    return;
                }
                ChatDocumentScopeState.SetEnabled(true);
                if (_documentsSwitch != null) _documentsSwitch.IsOn = true;
                UpdateDocumentsButtonVisual();
                if (InputBox != null) { InputBox.Text = template; InputBox.Focus(FocusState.Programmatic); InputBox.SelectionStart = Math.Clamp(caret ?? template.Length, 0, template.Length); }
                DocumentsButton?.Flyout?.Hide();
            };
            return button;
        }

        private void UpdateDocumentsButtonVisual()
        {
            if (DocumentsButton == null) return;
            bool on = ChatDocumentScopeState.Enabled;
            DocumentsButton.Background = on ? (Brush?)Application.Current.Resources["AccentFillColorDefaultBrush"] ?? new SolidColorBrush(Microsoft.UI.Colors.SteelBlue)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetHelpText(DocumentsButton, on ? "On" : "Off");
        }

        /// <summary>Synchronise the existing document toggle after a scoped
        /// Gaming guide handoff; the normal Chat source picker stays authoritative.</summary>
        public void RefreshGuideDocumentScopeVisual()
        {
            if (_documentsSwitch != null)
                _documentsSwitch.IsOn = ChatDocumentScopeState.Enabled;
            UpdateDocumentsButtonVisual();
        }

        private async Task LoadDocumentSourcesAsync()
        {
            if (_documentsSources == null || _documentsNote == null) return;
            int generation = ++_documentsSourceLoadGeneration;
            _documentsNote.Text = DT("Loading local folders…", "Caricamento delle cartelle locali…");
            try
            {
                List<RagSourceDto> sources = (await RagClient.Current.ListSourcesAsync(CancellationToken.None))
                    .Where(x => x.Enabled).ToList();
                if (generation != _documentsSourceLoadGeneration) return;
                _documentsAvailableSources = sources;
                ChatDocumentScopeState.SetKnownSources(sources.Select(x => (x.Id, x.FolderPath)));
                // A removed last selected source cannot silently become the
                // wire protocol's empty=all scope.
                if (ChatDocumentScopeState.Prune(sources.Select(x => x.Id)))
                {
                    _documentsScopeRevoked = true;
                    RefreshGuideDocumentScopeVisual();
                }
                RenderDocumentSourcePage();
            }
            catch (Exception ex)
            {
                if (generation == _documentsSourceLoadGeneration)
                    _documentsNote.Text = DT("Could not read the folder list: ", "Impossibile leggere l'elenco cartelle: ") + ex.Message;
            }
        }

        private void RenderDocumentSourcePage()
        {
            if (_documentsSources == null || _documentsSourcePageLabel == null) return;
            DocumentSourcePage page = DocumentSourcePicker.Filter(
                _documentsAvailableSources, _documentsSourceFilter?.Text, _documentsPageIndex);
            _documentsPageIndex = page.PageIndex;
            _documentsSources.Children.Clear();
            foreach (RagSourceDto source in page.Visible)
            {
                string path = source.FolderPath;
                string label = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
                var box = new CheckBox { Content = label, IsChecked = ChatDocumentScopeState.IsSelected(source.Id), MinHeight = 36 };
                ToolTipService.SetToolTip(box, path);
                AutomationProperties.SetName(box, path);
                AutomationProperties.SetAutomationId(box, "Chat_Documents_Source_" + source.Id);
                string id = source.Id;
                box.Checked += (_, _) =>
                {
                    ChatDocumentScopeState.SetSource(id, true);
                    _documentsScopeRevoked = false;
                    UpdateDocumentScopeSummary();
                };
                box.Unchecked += (_, _) =>
                {
                    bool wasEnabled = ChatDocumentScopeState.Enabled;
                    ChatDocumentScopeState.SetSource(id, false);
                    if (wasEnabled && !ChatDocumentScopeState.Enabled)
                    {
                        _documentsScopeRevoked = true;
                        RefreshGuideDocumentScopeVisual();
                    }
                    UpdateDocumentScopeSummary();
                };
                _documentsSources.Children.Add(box);
            }
            if (page.TotalMatches == 0)
                _documentsSources.Children.Add(new TextBlock
                {
                    Text = DT("No matching folder.", "Nessuna cartella corrispondente."),
                    Opacity = 0.8, TextWrapping = TextWrapping.Wrap
                });
            if (_documentsPreviousPage != null) _documentsPreviousPage.IsEnabled = page.PageIndex > 0;
            if (_documentsNextPage != null) _documentsNextPage.IsEnabled = page.PageIndex + 1 < page.PageCount;
            _documentsSourcePageLabel.Text = page.TotalMatches + " · "
                + (page.PageIndex + 1) + "/" + page.PageCount;
            UpdateDocumentScopeSummary();
        }

        private void UpdateDocumentScopeSummary()
        {
            if (_documentsNote == null) return;
            int selected = ChatDocumentScopeState.SelectedCount;
            if (_documentsScopeRevoked)
                _documentsNote.Text = DT(
                    "The last selected folder is no longer in scope. Documents were turned off to prevent searching all folders. Choose a folder and turn Documents on again.",
                    "L'ultima cartella selezionata non è più nell'ambito. Documenti è stato disattivato per evitare di cercare in tutte le cartelle. Scegli una cartella e riattiva Documenti.");
            else if (_documentsAvailableSources.Count == 0)
                _documentsNote.Text = DT("No enabled folder yet. Add one in Document search.",
                    "Nessuna cartella attiva. Aggiungine una in Ricerca documenti.");
            else if (!ChatDocumentScopeState.Enabled)
                _documentsNote.Text = DT("Document answers are off. Turn on the switch to use these sources.",
                    "Le risposte dai documenti sono disattivate. Attiva l'interruttore per usare queste fonti.");
            else if (selected == 0)
                _documentsNote.Text = DT("Using ALL ", "Usando TUTTE le ") + _documentsAvailableSources.Count
                    + DT(" enabled folders. Choose one or more to restrict the scope.",
                        " cartelle attive. Selezionane una o più per limitare l'ambito.");
            else
                _documentsNote.Text = selected + DT(" selected folder(s). Answers must cite the supporting source.",
                    " cartelle selezionate. Le risposte devono citare la fonte.");
        }
    }
}
