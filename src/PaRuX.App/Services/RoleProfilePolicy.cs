using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services;

/// <summary>The five plain-language "Use for" profiles shown to a person. Documents has no model slot of its own: it answers
/// with the Chat model (search and indexing use the built-in embedding engine).</summary>
public enum RoleProfile { Chat, Coding, Reasoning, Vision, Documents }

/// <summary>An installed text model, reduced to the facts the policy needs. Capabilities come from the shared
/// <c>ModelCategoryClassifier</c> taxonomy (read by the caller), never from a second keyword list here.</summary>
public sealed record RoleModelCandidate(
    string Path,
    string Name,
    long SizeBytes,
    bool Coding,
    bool Reasoning,
    bool Vision,
    bool HasVisionProjector,
    bool Uncensored,
    bool ExistsOnDisk = true);

/// <summary>What a role is assigned to right now (from the existing role store).</summary>
public sealed record RoleCurrentAssignment(string Role, string? ModelPath, bool IsUserChoice);

public enum RoleRecommendationKind
{
    /// <summary>A model was picked for this profile.</summary>
    Picked,
    /// <summary>No specialised model: the profile uses the Chat model.</summary>
    UsesChat,
    /// <summary>Nothing suitable is installed; the profile stays unavailable (no pretending).</summary>
    NoModel
}

public sealed record RoleRecommendation(
    RoleProfile Profile,
    string RoleKey,
    RoleRecommendationKind Kind,
    string? ModelPath,
    string? ModelName,
    string Explanation);

/// <summary>
/// Pure zero-config policy: installed models + detected hardware -> one recommended model per profile, each with a plain
/// explanation. It only reads facts and returns a recommendation; applying it goes through the existing role store, and a role
/// the person set themselves is never replaced by this policy (see <see cref="RoleProfilePresenter"/>).
/// </summary>
public static class RoleProfilePolicy
{
    public static readonly IReadOnlyList<RoleProfile> Order =
        new[] { RoleProfile.Chat, RoleProfile.Coding, RoleProfile.Reasoning, RoleProfile.Vision, RoleProfile.Documents };

    /// <summary>The existing role key a profile is stored under. Documents shares the Chat key (there is no slot of its own).</summary>
    public static string RoleKey(RoleProfile profile) => profile switch
    {
        RoleProfile.Coding => "coding",
        RoleProfile.Reasoning => "reasoning",
        RoleProfile.Vision => "vision",
        _ => "standard"
    };

    public static string Id(RoleProfile profile) => profile.ToString();

    public static string Title(RoleProfile profile, bool italian) => profile switch
    {
        RoleProfile.Chat => italian ? "Chat" : "Chat",
        RoleProfile.Coding => italian ? "Programmazione" : "Coding",
        RoleProfile.Reasoning => italian ? "Ragionamento" : "Reasoning",
        RoleProfile.Vision => italian ? "Immagini (vista)" : "Vision",
        _ => italian ? "Documenti" : "Documents"
    };

    public static IReadOnlyList<RoleRecommendation> Recommend(
        IReadOnlyList<RoleModelCandidate> installed, RuntimeManagerHardwareStatus? hardware, bool italian)
    {
        var usable = installed.Where(c => c.ExistsOnDisk && !string.IsNullOrWhiteSpace(c.Path)).ToList();
        RoleModelCandidate? chat = PickChat(usable, hardware);

        var result = new List<RoleRecommendation>();
        foreach (RoleProfile profile in Order)
        {
            result.Add(profile switch
            {
                RoleProfile.Chat => ForChat(chat, hardware, italian),
                RoleProfile.Coding => ForSpecialist(profile, usable, chat, hardware, c => c.Coding, italian),
                RoleProfile.Reasoning => ForSpecialist(profile, usable, chat, hardware, c => c.Reasoning, italian),
                RoleProfile.Vision => ForVision(usable, hardware, italian),
                _ => ForDocuments(chat, italian)
            });
        }
        return result;
    }

    // ---- selection -------------------------------------------------------------------------------------------------------

    /// <summary>Best hardware tier first (fits, tight, unknown, too big), then the larger model: more capable within what runs well.</summary>
    private static IOrderedEnumerable<RoleModelCandidate> Rank(IEnumerable<RoleModelCandidate> candidates, RuntimeManagerHardwareStatus? hardware) =>
        candidates.OrderBy(c => TierRank(c, hardware)).ThenByDescending(c => c.SizeBytes).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    private static int TierRank(RoleModelCandidate c, RuntimeManagerHardwareStatus? hardware) =>
        Verdict(c, hardware) switch
        {
            ModelFitVerdict.Fits => 0,
            ModelFitVerdict.Tight => 1,
            ModelFitVerdict.Unknown => 2,
            _ => 3
        };

    private static ModelFitVerdict Verdict(RoleModelCandidate c, RuntimeManagerHardwareStatus? hardware) =>
        hardware == null ? ModelFitVerdict.Unknown : ModelHardwareFitEstimator.Estimate(c.SizeBytes, hardware).Verdict;

    /// <summary>General chat prefers a general model; a coding or vision specialist is only used when it is all there is. Uncensored
    /// variants have their own role and are never the default.</summary>
    private static RoleModelCandidate? PickChat(IReadOnlyList<RoleModelCandidate> usable, RuntimeManagerHardwareStatus? hardware)
    {
        var standard = usable.Where(c => !c.Uncensored).ToList();
        var general = standard.Where(c => !c.Coding).ToList();
        IReadOnlyList<RoleModelCandidate> pool = general.Count > 0 ? general : standard.Count > 0 ? standard : usable;
        return Rank(pool, hardware).FirstOrDefault();
    }

    // ---- per profile -----------------------------------------------------------------------------------------------------

    private static RoleRecommendation ForChat(RoleModelCandidate? chat, RuntimeManagerHardwareStatus? hardware, bool it)
    {
        if (chat == null)
            return new RoleRecommendation(RoleProfile.Chat, RoleKey(RoleProfile.Chat), RoleRecommendationKind.NoModel, null, null,
                it ? "Nessun modello di chat installato. Scarica un modello dalla scheda Chat della Start board."
                   : "No chat model is installed yet. Download one from the Chat card of the Start board.");
        return new RoleRecommendation(RoleProfile.Chat, RoleKey(RoleProfile.Chat), RoleRecommendationKind.Picked, chat.Path, chat.Name,
            (it ? "Il modello generico più capace che gira bene su questo PC" : "The most capable general model that runs well on this PC")
            + FitSuffix(chat, hardware, it) + ".");
    }

    private static RoleRecommendation ForSpecialist(
        RoleProfile profile, IReadOnlyList<RoleModelCandidate> usable, RoleModelCandidate? chat, RuntimeManagerHardwareStatus? hardware,
        Func<RoleModelCandidate, bool> isSpecialist, bool it)
    {
        RoleModelCandidate? pick = Rank(usable.Where(c => isSpecialist(c) && !c.Uncensored), hardware).FirstOrDefault();
        string label = profile == RoleProfile.Coding ? (it ? "programmazione" : "coding") : (it ? "ragionamento" : "reasoning");
        if (pick != null && TierRank(pick, hardware) <= 2)
            return new RoleRecommendation(profile, RoleKey(profile), RoleRecommendationKind.Picked, pick.Path, pick.Name,
                (it ? $"Modello specializzato per {label} che gira bene su questo PC" : $"A {label}-specific model that runs well on this PC")
                + FitSuffix(pick, hardware, it) + ".");
        if (chat != null)
        {
            string why = pick != null
                ? (it ? $"Il modello per {label} installato è troppo grande per questo PC, quindi uso il modello di Chat."
                      : $"The installed {label} model is too large for this PC, so the Chat model is used.")
                : (it ? $"Nessun modello per {label} installato: uso il modello di Chat."
                      : $"No {label}-specific model is installed, so the Chat model is used.");
            return new RoleRecommendation(profile, RoleKey(profile), RoleRecommendationKind.UsesChat, chat.Path, chat.Name, why);
        }
        return new RoleRecommendation(profile, RoleKey(profile), RoleRecommendationKind.NoModel, null, null,
            it ? "Nessun modello installato." : "No model is installed.");
    }

    private static RoleRecommendation ForVision(IReadOnlyList<RoleModelCandidate> usable, RuntimeManagerHardwareStatus? hardware, bool it)
    {
        // A vision model without its projector cannot take an image, so it is never recommended; and there is no text fallback,
        // because a text-only Chat model cannot see.
        RoleModelCandidate? pick = Rank(usable.Where(c => c.Vision && c.HasVisionProjector && !c.Uncensored), hardware)
            .FirstOrDefault(c => TierRank(c, hardware) <= 2);
        if (pick != null)
            return new RoleRecommendation(RoleProfile.Vision, RoleKey(RoleProfile.Vision), RoleRecommendationKind.Picked, pick.Path, pick.Name,
                (it ? "Modello che sa leggere le immagini, con il suo file di visione, adatto a questo PC" : "A model that can read images, with its vision file, suited to this PC")
                + FitSuffix(pick, hardware, it) + ".");
        bool hasVisionWithoutProjector = usable.Any(c => c.Vision && !c.HasVisionProjector);
        return new RoleRecommendation(RoleProfile.Vision, RoleKey(RoleProfile.Vision), RoleRecommendationKind.NoModel, null, null,
            hasVisionWithoutProjector
                ? (it ? "Hai un modello con visione ma manca il suo file di visione, quindi non può leggere immagini."
                      : "A vision model is installed but its vision file is missing, so it cannot read images.")
                : (it ? "Nessun modello con visione installato. Un modello di solo testo non può leggere immagini."
                      : "No vision model is installed. A text-only model cannot read images."));
    }

    private static RoleRecommendation ForDocuments(RoleModelCandidate? chat, bool it)
    {
        if (chat == null)
            return new RoleRecommendation(RoleProfile.Documents, RoleKey(RoleProfile.Documents), RoleRecommendationKind.NoModel, null, null,
                it ? "La ricerca nei documenti funziona anche senza modello; per le risposte serve un modello di Chat."
                   : "Searching documents works without a model; answers need a Chat model.");
        return new RoleRecommendation(RoleProfile.Documents, RoleKey(RoleProfile.Documents), RoleRecommendationKind.UsesChat, chat.Path, chat.Name,
            it ? "Risponde con il modello di Chat; ricerca e indicizzazione usano il motore di embedding integrato e citano le fonti."
               : "Answers with the Chat model; search and indexing use the built-in embedding engine and cite their sources.");
    }

    private static string FitSuffix(RoleModelCandidate c, RuntimeManagerHardwareStatus? hardware, bool it) => Verdict(c, hardware) switch
    {
        ModelFitVerdict.Fits => it ? " (ci sta bene)" : " (fits comfortably)",
        ModelFitVerdict.Tight => it ? " (al limite per la memoria)" : " (tight on memory)",
        ModelFitVerdict.Big => it ? " (più lento: usa anche la RAM)" : " (slower: also uses RAM)",
        _ => string.Empty
    };
}
