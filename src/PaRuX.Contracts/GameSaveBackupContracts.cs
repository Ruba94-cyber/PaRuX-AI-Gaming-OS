namespace PaRuX.Contracts;

/// <summary>Only a local folder explicitly selected by the desktop owner. Never scans save folders automatically.</summary>
public sealed record GameSaveBackupRequest(string SourceFolder, string Label);

/// <summary>Asks for read-only save-folder hints for one game. The name is used as one folder name only; the Steam app id is digits only.</summary>
public sealed record GameSaveHintRequest(string GameName, string? SteamAppId = null);

/// <summary>An existing folder that probably holds the game's saves. Source is "documents", "saved-games", "appdata-local",
/// "appdata-roaming", "appdata-locallow", "known-title" or "steam-cloud". Files is capped (see FilesCapped). Nothing was read or changed.</summary>
public sealed record GameSaveHintDto(string Path, string Source, int Files, bool FilesCapped, DateTime? NewestWriteUtc);

/// <summary>Local PaRuX-owned archive; does not include the original source path.</summary>
public sealed record GameSaveBackupDto(string Id, string Label, int Files, long Bytes, DateTime CreatedUtc, string ArchiveSha256);

/// <summary>Validation of the manifest, archive checksum and every contained file hash.</summary>
public sealed record GameSaveVerificationDto(string Id, bool Valid, string Message, int VerifiedFiles);

/// <summary>Restore target chosen by the desktop owner. <paramref name="Confirm"/> must be true to write anything.</summary>
public sealed record GameSaveRestoreRequest(string TargetFolder, bool Confirm = false);

/// <summary>One file of a restore preview. Action is "create", "overwrite" or "unchanged".</summary>
public sealed record GameSaveRestoreItemDto(string Name, long Bytes, string Action);

/// <summary>Dry run: nothing is written. Ok is false when the archive does not verify or the target is unsafe.</summary>
public sealed record GameSaveRestorePlanDto(string Id, bool Ok, string Message, IReadOnlyList<GameSaveRestoreItemDto> Items);

/// <summary>Outcome of a confirmed restore. PreRestoreBackupId names the automatic backup of the target made first (null when the target held no files).</summary>
public sealed record GameSaveRestoreResultDto(string Id, bool Success, string Message, int FilesWritten, string? PreRestoreBackupId);

/// <summary>The folder (cloud-synced, NAS or USB) the save backups are mirrored to; empty when none is chosen.</summary>
public sealed record GameSaveSyncStatusDto(string Folder, bool Configured);

/// <summary>Choose the mirror folder; an empty path turns mirroring off.</summary>
public sealed record GameSaveSyncFolderRequest(string? Folder);

/// <summary>What one sync did: backups copied out, backups brought in, backups both sides already had, and anything skipped with the reason.</summary>
public sealed record GameSaveSyncResultDto(int Pushed, int Pulled, int AlreadyThere, IReadOnlyList<string> Problems);
