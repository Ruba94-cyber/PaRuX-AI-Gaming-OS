namespace PaRuX.Contracts;

/// <summary>
/// Source-level capability discovery, not a claim of universal runtime support.
/// A game declares which asset/content adapter it knows how to consume.
/// </summary>
public sealed record GameModAdapterDto(
    string Id, string Name, string AssetTypes,
    bool RequiresPausedGame, bool CanDirectApply, bool CanHotReload,
    string Delivery, string Limitation);

/// <summary>
/// Generic asset editing uses an existing PNG texture as a UV-preserving base.
/// RelativeAssetPath is the path expected by the game's OWN supported pack/mod
/// format. The source PNG is never replaced by PaRuX.
/// </summary>
public sealed record GameAssetModDraftRequest(
    string GameName, string RelativeAssetPath, string OriginalPngBase64, string Prompt);

public sealed record GameAssetModDraftDto(
    string DraftId, string GameName, string RelativeAssetPath,
    string OriginalSha256, string UpdatedSha256, string PreviewPngBase64,
    int Width, int Height, long ChangedPixels, DateTime ExpiresUtc,
    string Advisory);

public sealed record GameAssetModExportRequest(string DraftId, bool Consent);

public sealed record GameAssetModExportDto(
    string BundlePath, string BundleSha256, string RelativeAssetPath,
    string OriginalSha256, string UpdatedSha256, string Advisory);
