namespace PaRuX.Contracts;

/// <summary>
/// An AI may propose a temporary plain-text config change, but the owner must
/// inspect the preview before any game file changes. Only local managed portable
/// games paused by PaRuX Quick Resume are eligible in this initial adapter.
/// </summary>
public sealed record GameModProposalRequest(
    string ManualGameId, int ProcessId, DateTime StartedUtc,
    string RelativePath, string ProposedText, bool OfflineSinglePlayerConfirmed);

public sealed record GameModPreviewDto(
    string PreviewId, string GameName, string RelativePath,
    string CurrentSha256, string OriginalText, string ProposedText,
    DateTime ExpiresUtc, string Advisory);

public sealed record GameModApplyRequest(string PreviewId, bool Consent);
public sealed record GameModRestoreRequest(string ModId, bool Consent);

public sealed record GameTemporaryModDto(
    string Id, string GameName, string RelativePath, DateTime AppliedUtc,
    string OriginalSha256, string ModifiedSha256, string Advisory);
