namespace PaRuX.Contracts;

/// <summary>
/// Localized, first-party text for the Windows Quick Chat presenter.
/// Does not change its conversation, resources or message transport.
/// </summary>
public sealed record QuickChatStrings(
    string WindowTitle,
    string OpenInMain,
    string OpenHelp,
    string CloseHelp,
    string CloseAutomation,
    string SendHelp,
    string SendAutomation,
    string EmptyTitle,
    string EmptyDescription,
    string InputPlaceholder)
{
    public static QuickChatStrings ForLanguage(string? language) =>
        string.Equals(language, "it", StringComparison.OrdinalIgnoreCase)
        ? new(
            "PaRuX — Chat rapida",
            "Apri in PaRuX",
            "Continua questa conversazione nella finestra principale",
            "Chiudi (Esc)",
            "Chiudi la Chat rapida",
            "Invia (Invio)",
            "Invia dalla Chat rapida",
            "Chiedimi qualcosa",
            "Le risposte rimangono su questo PC.",
            "Una domanda veloce…")
        : new(
            "PaRuX Quick Chat",
            "Open in PaRuX",
            "Continue this conversation in the main window",
            "Close (Esc)",
            "Close Quick Chat",
            "Send (Enter)",
            "Send from Quick Chat",
            "Ask me something",
            "Answers stay on this machine.",
            "Quick question…");
}
