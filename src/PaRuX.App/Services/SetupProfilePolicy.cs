using System;

namespace PaRuX.Services;

/// <summary>What the person wants PaRuX for on this PC. Chosen at first run, changeable later in Settings.</summary>
public enum SetupProfile { Both, AiOnly, GamingOnly }

/// <summary>What a <see cref="SetupProfile"/> switches on. One flag per real consumer, so start-up, onboarding and the
/// visible workspaces all read the same decision.</summary>
public sealed record SetupProfilePlan(
    bool StartLocalAi,
    bool StartGaming,
    bool StartAgentWorkers,
    bool RequireLanguageModel,
    bool ShowAiWorkspaces,
    bool ShowGamingWorkspace,
    bool ShowModeTabs,
    bool StartInGamingTab);

/// <summary>
/// Pure policy of the modular setup (AI only / Gaming only / Both). Both is the default and the value for every existing
/// installation, so nothing changes for people who already use PaRuX. Switching profiles only changes what starts and what is
/// shown: it never deletes models, history, game library, settings or indexes, and never reinstalls anything.
/// </summary>
public static class SetupProfilePolicy
{
    public const string BothId = "both";
    public const string AiOnlyId = "ai";
    public const string GamingOnlyId = "gaming";

    /// <summary>Empty, missing or unknown values mean Both: an existing installation keeps everything it has.</summary>
    public static SetupProfile Parse(string? stored) =>
        PaRuX.Contracts.CorporateMode.IsActive ? SetupProfile.AiOnly     // Corporate Lite is AI/documents only
        : ParseStored(stored);

    private static SetupProfile ParseStored(string? stored) => (stored ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        AiOnlyId => SetupProfile.AiOnly,
        GamingOnlyId => SetupProfile.GamingOnly,
        _ => SetupProfile.Both
    };

    public static string ToStorage(SetupProfile profile) => profile switch
    {
        SetupProfile.AiOnly => AiOnlyId,
        SetupProfile.GamingOnly => GamingOnlyId,
        _ => BothId
    };

    public static SetupProfilePlan Plan(SetupProfile profile) => profile switch
    {
        SetupProfile.AiOnly => new SetupProfilePlan(
            StartLocalAi: true, StartGaming: false, StartAgentWorkers: true, RequireLanguageModel: true,
            ShowAiWorkspaces: true, ShowGamingWorkspace: false, ShowModeTabs: false, StartInGamingTab: false),
        SetupProfile.GamingOnly => new SetupProfilePlan(
            StartLocalAi: false, StartGaming: true, StartAgentWorkers: false, RequireLanguageModel: false,
            ShowAiWorkspaces: false, ShowGamingWorkspace: true, ShowModeTabs: false, StartInGamingTab: true),
        _ => new SetupProfilePlan(
            StartLocalAi: true, StartGaming: true, StartAgentWorkers: true, RequireLanguageModel: true,
            ShowAiWorkspaces: true, ShowGamingWorkspace: true, ShowModeTabs: true, StartInGamingTab: false)
    };

    public static string Label(SetupProfile profile, bool italian) => profile switch
    {
        SetupProfile.AiOnly => italian ? "Solo IA" : "AI only",
        SetupProfile.GamingOnly => italian ? "Solo Gaming" : "Gaming only",
        _ => italian ? "Entrambi" : "Both"
    };

    public static string Description(SetupProfile profile, bool italian) => profile switch
    {
        SetupProfile.AiOnly => italian
            ? "Chat, documenti, immagini e codice in locale. I servizi di gioco non vengono avviati."
            : "Chat, documents, images and code on this PC. The gaming services are not started.",
        SetupProfile.GamingOnly => italian
            ? "Libreria giochi, controller e modalità TV. Non serve nessun modello linguistico."
            : "Game library, controllers and TV mode. No language model is needed.",
        _ => italian
            ? "IA e giochi insieme, con le risorse del PC condivise: l'IA si fa da parte mentre giochi."
            : "AI and games together, sharing the PC's resources: the AI steps aside while you play."
    };

    /// <summary>The reassurance shown next to the profile switch in Settings.</summary>
    public static string SwitchNote(bool italian) => italian
        ? "Cambiare scelta non cancella nulla: modelli, cronologia, giochi e impostazioni restano dove sono e non serve reinstallare."
        : "Changing this deletes nothing: models, history, games and settings stay where they are and nothing is reinstalled.";
}
