using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PaRuX.Views;

public sealed partial class ChatView
{
    private void EmptyState_Loaded(object sender, RoutedEventArgs e)
    {
        bool it = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
        bool corporate = PaRuX.Contracts.CorporateMode.IsActive;
        HomeHeadline.Text = corporate
            ? (it ? "Come posso aiutarti?" : "How can I help?")
            : (it ? "Chiedi qualcosa a PaRuX" : "Ask PaRuX anything");
        HomeDescription.Text = corporate
            ? (it ? "Fai una domanda o lavora con i documenti autorizzati. L'elaborazione è locale sul PC."
                  : "Ask a question or work with approved documents. Processing stays local to this PC.")
            : (it ? "Inizia una conversazione con PaRuX." : "Start a conversation with PaRuX.");
        SetHomeSuggestion(HomeCodeSuggestion, HomeCodeLabel,
            it ? "Migliora questo progetto" : "Improve this codebase",
            it ? "Migliora il codice di questo progetto:" : "Improve the code in this project:");
        SetHomeSuggestion(HomeAnalysisSuggestion, HomeAnalysisLabel,
            corporate
                ? (it ? "Analizza un quesito con chiarezza" : "Analyse a question clearly")
                : (it ? "Analizza un problema in profondità" : "Analyse a problem in depth"),
            corporate
                ? (it ? "Analizza questo quesito, distinguendo fatti, ipotesi e informazioni mancanti:"
                      : "Analyse this question, distinguishing facts, assumptions and missing information:")
                : (it ? "Analizza questo problema in profondità:" : "Analyse this problem in depth:"));
        SetHomeSuggestion(HomeSummarySuggestion, HomeSummaryLabel,
            corporate
                ? (it ? "Riassumi un testo o un documento" : "Summarise a text or document")
                : (it ? "Riassumi un testo nei punti principali" : "Summarise a text into key points"),
            corporate
                ? (it ? "Riassumi il seguente testo, indicando fatti principali, scadenze e punti da verificare:"
                      : "Summarise the following text, listing key facts, deadlines and items to verify:")
                : (it ? "Riassumi questo testo nei punti principali:" : "Summarise this into key points:"));
        SetHomeSuggestion(HomeImageSuggestion, HomeImageLabel,
            it ? "Genera un'immagine" : "Generate an image",
            it ? "Genera un'immagine di un paesaggio futuristico, senza testo" : "Generate an image of a futuristic landscape, no text");
    }

    private static void SetHomeSuggestion(Button button, TextBlock label, string title, string prompt)
    {
        label.Text = title;
        button.Tag = prompt;
        AutomationProperties.SetName(button, title);
    }
}
