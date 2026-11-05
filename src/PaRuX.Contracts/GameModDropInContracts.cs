namespace PaRuX.Contracts;

/// <summary>
/// Optional ZIP-drop-in adapter. The user has independently verified their
/// particular offline game accepts standalone ZIP mods in this exact directory.
/// This does not grant generic compatibility or run installers/scripts.
/// </summary>
public sealed record GameModDropInInstallRequest(
    string BundlePath, string BundleSha256, string TargetModFolder,
    bool CompatibleZipLoaderConfirmed, bool OfflineGameClosedConfirmed);

/// <summary>Explicit owner-only removal; it refuses changed files.</summary>
public sealed record GameModDropInRemoveRequest(
    string ReceiptId, bool Consent, bool OfflineGameClosedConfirmed);

public sealed record GameModDropInReceiptDto(
    string Id, string GameName, string ModFolder, string InstalledFile,
    string BundleSha256, string InstalledSha256, DateTime CreatedUtc,
    string State, string Advisory);
