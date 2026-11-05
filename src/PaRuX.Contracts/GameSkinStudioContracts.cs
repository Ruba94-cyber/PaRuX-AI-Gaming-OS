namespace PaRuX.Contracts;

/// <summary>
/// Local-only Minecraft Java skin draft. A generated UV atlas is a starting point;
/// PaRuX cannot guarantee correct Minecraft UV placement or change an account skin.
/// Import remains a separate explicit user action in the official skin selector.
/// </summary>
public sealed record GameSkinDraftRequest(string Prompt, string ArmStyle = "classic");

public sealed record GameSkinDraftDto(
    string DraftId, string ArmStyle, string PngBase64, DateTime ExpiresUtc, string Advisory);

public sealed record GameSkinExportRequest(string DraftId, bool Consent);

public sealed record GameSkinExportDto(
    string FilePath, string Sha256, string ArmStyle, string Advisory);
