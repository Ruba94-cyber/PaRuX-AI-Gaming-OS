using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services;

public enum RoleRowState
{
    /// <summary>The role already uses the recommendation (or, for a fallback to Chat, has no separate choice).</summary>
    Matches,
    /// <summary>The recommendation differs from what is set and can be applied.</summary>
    CanApply,
    /// <summary>The person chose a different model themselves: kept, never replaced automatically.</summary>
    YourChoice,
    /// <summary>Nothing suitable is installed.</summary>
    Unavailable,
    /// <summary>Documents: follows the Chat row.</summary>
    FollowsChat
}

public sealed record RoleOverrideChoice(string Path, string Name);

/// <summary>One "Use for" row. <see cref="AutomationPrefix"/> gives the stable ids: <c>{prefix}_Row</c>, <c>_Model</c>, <c>_Explain</c>,
/// <c>_Apply</c>, <c>_Override</c>.</summary>
public sealed record RoleProfileRow(
    RoleProfile Profile,
    string Title,
    string AutomationPrefix,
    string RecommendedText,
    string CurrentText,
    string Explanation,
    RoleRowState State,
    string StateText,
    bool CanApply,
    string? RecommendedPath,
    string RoleKey,
    IReadOnlyList<RoleOverrideChoice> AdvancedChoices);

public sealed record RoleProfileBoard(IReadOnlyList<RoleProfileRow> Rows, string Summary, bool CanApplyAll, bool ShowAdvanced)
{
    public const string PanelAutomationId = "RoleProfile_Panel";
    public const string ApplyAllAutomationId = "RoleProfile_ApplyRecommended";
    public const string SummaryAutomationId = "RoleProfile_Summary";
    public const string AdvancedToggleAutomationId = "RoleProfile_Advanced";
}

/// <summary>One change to the existing role store (<c>POST /models/roles/{role}</c>).</summary>
public sealed record RoleApplyOperation(string RoleKey, string ModelPath);

/// <summary>
/// Pure presenter of the "Use for" selector (Model Hub Start board and Settings). It joins the policy's recommendations with the
/// role store's current state and decides, per row, what can be applied. "Apply recommended" never touches a role the person set
/// themselves; the Advanced override lets them pick any installed model per role (an explicit choice, stored like any other).
/// </summary>
public static class RoleProfilePresenter
{
    public static RoleProfileBoard Build(
        IReadOnlyList<RoleRecommendation> recommendations,
        IReadOnlyList<RoleCurrentAssignment> current,
        IReadOnlyList<RoleModelCandidate> installed,
        bool advanced,
        bool italian)
    {
        var rows = new List<RoleProfileRow>();
        foreach (RoleRecommendation rec in recommendations)
        {
            RoleCurrentAssignment? now = current.FirstOrDefault(c => string.Equals(c.Role, rec.RoleKey, StringComparison.OrdinalIgnoreCase));
            string currentName = NameOf(now?.ModelPath, installed) ?? (italian ? "automatico" : "automatic");
            bool same = now?.ModelPath != null && rec.ModelPath != null
                && string.Equals(now.ModelPath, rec.ModelPath, StringComparison.OrdinalIgnoreCase);
            bool userChoice = now is { IsUserChoice: true } && !string.IsNullOrWhiteSpace(now.ModelPath);

            RoleRowState state;
            if (rec.Profile == RoleProfile.Documents) state = RoleRowState.FollowsChat;
            else if (rec.Kind == RoleRecommendationKind.NoModel) state = RoleRowState.Unavailable;
            else if (rec.Kind == RoleRecommendationKind.UsesChat) state = userChoice ? RoleRowState.YourChoice : RoleRowState.Matches;
            else if (same) state = RoleRowState.Matches;
            else state = userChoice ? RoleRowState.YourChoice : RoleRowState.CanApply;

            // Documents shows what Chat currently uses.
            if (rec.Profile == RoleProfile.Documents)
            {
                RoleCurrentAssignment? chat = current.FirstOrDefault(c => string.Equals(c.Role, "standard", StringComparison.OrdinalIgnoreCase));
                currentName = NameOf(chat?.ModelPath, installed) ?? (italian ? "automatico" : "automatic");
            }

            string recommended = rec.ModelName ?? (italian ? "nessuno" : "none");
            IReadOnlyList<RoleOverrideChoice> choices = advanced && rec.Profile != RoleProfile.Documents
                ? ChoicesFor(rec.Profile, installed)
                : Array.Empty<RoleOverrideChoice>();
            rows.Add(new RoleProfileRow(
                rec.Profile, RoleProfilePolicy.Title(rec.Profile, italian), "RoleProfile_" + RoleProfilePolicy.Id(rec.Profile),
                recommended, currentName, rec.Explanation, state, StateText(state, italian),
                state == RoleRowState.CanApply || (state == RoleRowState.YourChoice && rec.Kind == RoleRecommendationKind.Picked),
                rec.ModelPath, rec.RoleKey, choices));
        }

        int pending = rows.Count(r => r.State == RoleRowState.CanApply);
        string summary = pending == 0
            ? (italian ? "Ogni uso ha già il modello consigliato." : "Every use already has its recommended model.")
            : (italian ? $"{pending} uso/i possono passare al modello consigliato." : $"{pending} use(s) can switch to the recommended model.");
        if (rows.All(r => r.State is RoleRowState.Unavailable or RoleRowState.FollowsChat))
            summary = italian ? "Installa un modello per iniziare." : "Install a model to get started.";
        return new RoleProfileBoard(rows, summary, pending > 0, advanced);
    }

    /// <summary>"Apply recommended": only rows that can be applied; a role the person set stays as it is.</summary>
    public static IReadOnlyList<RoleApplyOperation> PlanApplyAll(RoleProfileBoard board) =>
        board.Rows.Where(r => r.State == RoleRowState.CanApply && !string.IsNullOrWhiteSpace(r.RecommendedPath))
            .Select(r => new RoleApplyOperation(r.RoleKey, r.RecommendedPath!)).ToList();

    /// <summary>One row's own button: the explicit "Use recommended" of that row (also allowed over the person's own earlier choice,
    /// because they pressed it).</summary>
    public static RoleApplyOperation? PlanApplyRow(RoleProfileRow row) =>
        row.CanApply && !string.IsNullOrWhiteSpace(row.RecommendedPath) ? new RoleApplyOperation(row.RoleKey, row.RecommendedPath!) : null;

    /// <summary>Advanced override: any installed model the person picks for a row. Documents has no slot of its own.</summary>
    public static RoleApplyOperation? PlanOverride(RoleProfileRow row, string? chosenPath)
    {
        if (row.Profile == RoleProfile.Documents || string.IsNullOrWhiteSpace(chosenPath)) return null;
        return row.AdvancedChoices.Any(c => string.Equals(c.Path, chosenPath, StringComparison.OrdinalIgnoreCase))
            ? new RoleApplyOperation(row.RoleKey, chosenPath)
            : null;
    }

    public static string ApplyLabel(bool italian) => italian ? "Usa il consigliato" : "Use recommended";
    public static string ApplyAllLabel(bool italian) => italian ? "Applica i consigliati" : "Apply recommended";
    public static string AdvancedLabel(bool italian) => italian ? "Scelta manuale (avanzata)" : "Choose manually (advanced)";
    public static string Heading(bool italian) => italian ? "Usa per" : "Use for";
    public static string Intro(bool italian) => italian
        ? "PaRuX propone il modello migliore già installato per ogni uso, in base al tuo PC. Non scarica nulla e non cambia le scelte che hai fatto tu."
        : "PaRuX suggests the best model you already have for each use, based on this PC. It downloads nothing and never changes a choice you made.";

    private static IReadOnlyList<RoleOverrideChoice> ChoicesFor(RoleProfile profile, IReadOnlyList<RoleModelCandidate> installed) =>
        installed.Where(c => c.ExistsOnDisk && (profile != RoleProfile.Vision || (c.Vision && c.HasVisionProjector)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new RoleOverrideChoice(c.Path, c.Name)).ToList();

    private static string? NameOf(string? path, IReadOnlyList<RoleModelCandidate> installed)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        RoleModelCandidate? hit = installed.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        return hit?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private static string StateText(RoleRowState state, bool it) => state switch
    {
        RoleRowState.Matches => it ? "Già impostato" : "Already set",
        RoleRowState.CanApply => it ? "Si può migliorare" : "Can be improved",
        RoleRowState.YourChoice => it ? "Scelta tua, non modificata" : "Your choice, left as is",
        RoleRowState.Unavailable => it ? "Non disponibile" : "Not available",
        _ => it ? "Segue la Chat" : "Follows Chat"
    };
}
