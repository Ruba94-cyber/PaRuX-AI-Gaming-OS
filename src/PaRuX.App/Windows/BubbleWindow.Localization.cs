using System.Globalization;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;

namespace PaRuX.Shell;

public sealed partial class BubbleWindow
{
    private void ApplyQuickChatLocalization()
    {
        QuickChatStrings text = QuickChatStrings.ForLanguage(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        Title = text.WindowTitle;
        BubbleOpenInMain.Content = text.OpenInMain;
        ToolTipService.SetToolTip(BubbleOpenInMain, text.OpenHelp);
        AutomationProperties.SetName(BubbleOpenInMain, text.OpenInMain);
        BubbleEmptyTitle.Text = text.EmptyTitle;
        BubbleEmptyDescription.Text = text.EmptyDescription;
        BubbleInput.PlaceholderText = text.InputPlaceholder;
        ToolTipService.SetToolTip(BubbleCollapse, text.CloseHelp);
        AutomationProperties.SetName(BubbleCollapse, text.CloseAutomation);
        ToolTipService.SetToolTip(BubbleSend, text.SendHelp);
        AutomationProperties.SetName(BubbleSend, text.SendAutomation);
    }
}
