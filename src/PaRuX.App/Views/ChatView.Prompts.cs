using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Prompt library in the composer: built-in starters plus the owner's saved prompts, with {{placeholders}} filled in a small
    /// form. Choosing a prompt only writes text into the composer; nothing is sent and no model is started.
    /// </summary>
    public sealed partial class ChatView
    {
        private readonly PromptLibraryStore _prompts = new(Path.Combine(AppPaths.PaRuXRoot, "prompt-library.json"));

        private void PromptsButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout();
            flyout.Content = BuildPromptsFlyoutContent(flyout);
            flyout.ShowAt((FrameworkElement)sender);
        }

        private UIElement BuildPromptsFlyoutContent(Flyout flyout)
        {
            var panel = new StackPanel { Spacing = 4, MinWidth = 300, MaxWidth = 380 };
            TextBlock Heading(string text) => new()
            {
                Text = text, Margin = new Thickness(0, 8, 0, 2), Opacity = 0.75,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };

            panel.Children.Add(Heading(ChatT("Starters", "Punti di partenza")));
            foreach (SavedPrompt starter in StarterPrompts.All(ChatItalian))
                panel.Children.Add(PromptRow(starter, flyout, deletable: false));

            IReadOnlyList<SavedPrompt> mine = _prompts.List();
            panel.Children.Add(Heading(mine.Count == 0 ? ChatT("Your prompts (none yet)", "I tuoi prompt (ancora nessuno)") : ChatT("Your prompts", "I tuoi prompt")));
            foreach (SavedPrompt prompt in mine)
                panel.Children.Add(PromptRow(prompt, flyout, deletable: true));

            var save = new Button
            {
                Content = ChatT("Save the text in the box as a prompt...", "Salva il testo nella casella come prompt..."),
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44, Margin = new Thickness(0, 8, 0, 0),
            };
            AutomationProperties.SetAutomationId(save, "Chat_Prompts_SaveCurrent_Button");
            save.Click += async (_, _) => { flyout.Hide(); await SaveCurrentAsPromptAsync(); };
            panel.Children.Add(save);
            return new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private UIElement PromptRow(SavedPrompt prompt, Flyout flyout, bool deletable)
        {
            var grid = new Grid { ColumnSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var use = new Button
            {
                Content = new TextBlock { Text = prompt.Title, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left },
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 44,
            };
            AutomationProperties.SetAutomationId(use, "Chat_Prompt_" + prompt.Id);
            AutomationProperties.SetName(use, prompt.Title);
            ToolTipService.SetToolTip(use, prompt.Text.Length > 300 ? prompt.Text[..300] + "…" : prompt.Text);
            use.Click += async (_, _) => { flyout.Hide(); await UsePromptAsync(prompt); };
            grid.Children.Add(use);
            if (deletable)
            {
                var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, MinHeight = 44, MinWidth = 44 };
                AutomationProperties.SetAutomationId(delete, "Chat_PromptDelete_" + prompt.Id);
                AutomationProperties.SetName(delete, ChatT("Delete prompt ", "Elimina il prompt ") + prompt.Title);
                delete.Click += (_, _) =>
                {
                    _prompts.Remove(prompt.Id);
                    flyout.Content = BuildPromptsFlyoutContent(flyout);
                };
                Grid.SetColumn(delete, 1);
                grid.Children.Add(delete);
            }
            return grid;
        }

        private async Task UsePromptAsync(SavedPrompt prompt)
        {
            IReadOnlyList<string> names = PromptTemplate.Variables(prompt.Text);
            string text = prompt.Text;
            if (names.Count > 0)
            {
                var fields = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
                var form = new StackPanel { Spacing = 10, MinWidth = 340 };
                foreach (string name in names)
                {
                    var box = new TextBox { Header = name, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 160 };
                    AutomationProperties.SetAutomationId(box, "Chat_PromptField_" + name);
                    fields[name] = box;
                    form.Children.Add(box);
                }
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = prompt.Title,
                    Content = new ScrollViewer { Content = form, MaxHeight = 480 },
                    PrimaryButtonText = ChatT("Put in the box", "Inserisci nella casella"),
                    CloseButtonText = ChatT("Cancel", "Annulla"),
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                text = PromptTemplate.Fill(text, fields.ToDictionary(p => p.Key, p => p.Value.Text ?? string.Empty, StringComparer.OrdinalIgnoreCase));
            }
            InputBox.Text = string.IsNullOrWhiteSpace(InputBox.Text) ? text : InputBox.Text.TrimEnd() + "\n\n" + text;
            InputBox.Focus(FocusState.Programmatic);
            InputBox.SelectionStart = InputBox.Text.Length;
        }

        private async Task SaveCurrentAsPromptAsync()
        {
            string current = (InputBox.Text ?? string.Empty).Trim();
            if (current.Length == 0)
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = ChatT("Nothing to save", "Niente da salvare"),
                    Content = ChatT("Write the prompt in the message box first. Use {{name}} for parts you will fill in each time.",
                        "Scrivi prima il prompt nella casella del messaggio. Usa {{nome}} per le parti da compilare ogni volta."),
                    CloseButtonText = ChatT("OK", "OK"),
                }.ShowAsync();
                return;
            }
            var title = new TextBox
            {
                Header = ChatT("Name", "Nome"), MaxLength = PromptLibraryStore.MaxTitle,
                Text = current.Length > 40 ? current[..40].Replace('\n', ' ') : current.Replace('\n', ' '),
            };
            AutomationProperties.SetAutomationId(title, "Chat_PromptSave_Name");
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ChatT("Save as a prompt", "Salva come prompt"),
                Content = title,
                PrimaryButtonText = ChatT("Save", "Salva"),
                CloseButtonText = ChatT("Cancel", "Annulla"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (_prompts.Save(title.Text, current) is null)
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = ChatT("Not saved", "Non salvato"),
                    Content = ChatT("The name must be 1-80 characters, the text at most 8,000, and the library holds up to 200 prompts.",
                        "Il nome deve avere 1-80 caratteri, il testo al massimo 8.000, e la libreria contiene fino a 200 prompt."),
                    CloseButtonText = ChatT("OK", "OK"),
                }.ShowAsync();
        }

        private static string ChatT(string en, string it) => ChatItalian ? it : en;
    }
}
