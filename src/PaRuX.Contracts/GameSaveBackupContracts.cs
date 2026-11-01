namespace PaRuX.Contracts;

/// <summary>Only a local folder explicitly selected by the desktop owner. Never scans save folders automatically.</summary>
public sealed record GameSaveBackupRequest(string SourceFolder, string Label);

/// <summary>Local PaRuX-owned archive; does not include the original source path.</summary>
public sealed record GameSaveBackupDto(string Id, string Label, int Files, long Bytes, DateTime CreatedUtc, string ArchiveSha256);

/// <summary>Validation of the manifest, archive checksum and every contained file hash.</summary>
public sealed record GameSaveVerificationDto(string Id, bool Valid, string Message, int VerifiedFiles);
