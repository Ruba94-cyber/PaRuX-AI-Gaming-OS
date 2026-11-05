namespace PaRuX.Contracts;

/// <summary>
/// Owner-only request to prepare an existing local Chat question about a
/// manually registered game, using only selected, indexed local sources.
/// No game paths, GPU override, Chat execution, or online wiki fetch.
/// </summary>
public sealed record LocalGameGuideRequest(
    string? ManualGameId,
    string? Question,
    IReadOnlyList<string>? SourceIds,
    bool Consent);

/// <summary>
/// Ready for the existing Chat endpoint. Caller must STILL choose to send
/// it, and must use exactly Documents (never an empty/all-source scope).
/// </summary>
public sealed record LocalGameGuidePreparationDto(
    string QuestionForChat, ChatDocumentScope Documents, string Explanation);
