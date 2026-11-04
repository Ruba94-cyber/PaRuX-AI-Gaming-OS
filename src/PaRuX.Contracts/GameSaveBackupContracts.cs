namespace PaRuX.Contracts;

/// <summary>Only a local folder explicitly selected by the desktop owner. Never scans save folders automatically.</summary>
public sealed record GameSaveBackupRequest(string SourceFolder, string Label);

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
