using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>A request prepared for the EXISTING Chat document flow; never runs inference itself.</summary>
public sealed record LocalGameChatPlan(
    bool Ready, string QuestionForChat, ChatDocumentScope? Documents, string Explanation);

public static class LocalGameChatPolicy
{
    /// <summary>
    /// Produces a consent-scoped Chat question about the current game and only the already indexed sources
    /// explicitly selected by the user. An empty scope must never mean "search all documents".
    /// </summary>
    public static LocalGameChatPlan Build(
        string? gameTitle, string? question, bool optedIntoDocumentSearch,
        IReadOnlyCollection<string>? approvedSourceIds)
    {
        if (!optedIntoDocumentSearch)
            return new(false, string.Empty, null, "Select local game guides in Chat to enable document answers.");
        string title = gameTitle?.Trim() ?? string.Empty;
        string prompt = question?.Trim() ?? string.Empty;
        if (title.Length == 0 || title.Length > 160 || title.Any(char.IsControl)
            || prompt.Length == 0 || prompt.Length > 3000)
            throw new InvalidDataException("Choose a game and enter a valid question.");
        // Source identifiers come from the already-indexed, user-consented source picker.
        // Never interpret an empty scope as "all documents", and cap malformed/oversized scopes.
        if (approvedSourceIds is null || approvedSourceIds.Count > 64
            || approvedSourceIds.Any(x => string.IsNullOrWhiteSpace(x)
                || x.Length > 200 || x.Any(char.IsControl)))
            return new(false, string.Empty, null, "Choose up to 64 valid indexed sources explicitly.");
        var sources = approvedSourceIds.Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sources.Count == 0)
            return new(false, string.Empty, null, "Choose at least one consented indexed local source.");
        return new(true, "For the game \"" + title + "\": " + prompt,
            new ChatDocumentScope { Enabled = true, SourceIds = sources },
            "Uses the existing local document answer path; cited sources only, no online wiki fetch.");
    }
}

public enum GameGpuAdmission { Allowed, BlockedByGaming, RequiresUserAction }

public sealed record GameGpuAdmissionDecision(GameGpuAdmission Status, string Explanation);

/// <summary>
/// Presentation policy layered over (never replacing) GamingHardOffWatch, WorkloadOrchestrator
/// and local inference admission. A user asking a question does not lift Gaming hard-off.
/// </summary>
public static class GameGpuAdmissionPolicy
{
    public static GameGpuAdmissionDecision Evaluate(bool gamingModeActive, bool gameProcessRunning,
        bool userExplicitlyAskedToReleaseGameResources)
    {
        if (!gamingModeActive && !gameProcessRunning)
            return new(GameGpuAdmission.Allowed, "No game activity reported; Core still checks resources.");
        if (!userExplicitlyAskedToReleaseGameResources)
            return new(GameGpuAdmission.BlockedByGaming,
                "The game has priority. PaRuX will not load AI onto its GPU.");
        return new(GameGpuAdmission.RequiresUserAction,
            "First pause or exit the game and exit Gaming Mode explicitly; Core must re-check before AI starts.");
    }
}
