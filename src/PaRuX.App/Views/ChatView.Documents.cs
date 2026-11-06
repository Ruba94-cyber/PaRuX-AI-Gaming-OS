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
                UpdateDocumentsButtonVisual();
            };
            _documentsNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12, MaxWidth = 320 };
            AutomationProperties.SetAutomationId(_documentsNote, "Chat_Documents_Note");
            _documentsSources = new StackPanel { Spacing = 2 };
            AutomationProperties.SetAutomationId(_documentsSources, "Chat_Documents_Sources");
            var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4) };
            panel.Children.Add(_documentsSwitch);
            panel.Children.Add(new TextBlock { Text = DT("Folders that may answer (none ticked = all)", "Cartelle che possono rispondere (nessuna = tutte)"), FontSize = 12, Opacity = 0.8 });
            panel.Children.Add(_documentsSources);
            panel.Children.Add(_documentsNote);
            panel.Children.Add(new TextBlock { Text = DT("Document skills", "Competenze sui documenti"), FontSize = 12, Opacity = 0.8 });
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
            try
            {
                List<RagSourceDto> sources = (await RagClient.Current.ListSourcesAsync(CancellationToken.None)).Where(x => x.Enabled).ToList();
                ChatDocumentScopeState.Prune(sources.Select(x => x.Id));
                _documentsSources.Children.Clear();
                foreach (RagSourceDto source in sources)
                {
                    var box = new CheckBox { Content = System.IO.Path.GetFileName(source.FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : source.FolderPath, IsChecked = ChatDocumentScopeState.IsSelected(source.Id) };
                    ToolTipService.SetToolTip(box, source.FolderPath);
                    AutomationProperties.SetAutomationId(box, "Chat_Documents_Source_" + source.Id);
                    string id = source.Id;
                    box.Checked += (s, e) => ChatDocumentScopeState.SetSource(id, true);
                    box.Unchecked += (s, e) => ChatDocumentScopeState.SetSource(id, false);
                    _documentsSources.Children.Add(box);
                }
                _documentsNote.Text = sources.Count == 0
                    ? DT("No folder is indexed yet. Add one in Document search or Settings > RAG.", "Nessuna cartella indicizzata. Aggiungine una in Ricerca documenti o in Impostazioni > RAG.")
                    : DT("Answers cite the file and page, sheet or slide. If the documents do not contain the answer, the chat says so.",
                        "Le risposte citano file e pagina, foglio o slide. Se i documenti non contengono la risposta, la chat lo dice.");
            }
            catch (Exception ex) { _documentsNote.Text = DT("Could not read the folder list: ", "Impossibile leggere l'elenco cartelle: ") + ex.Message; }
        }
    }
}
