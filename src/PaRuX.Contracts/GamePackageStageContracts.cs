namespace PaRuX.Contracts;

/// <summary>
/// Copy-only local package staging. The expected hash must come from a trusted
/// independent publisher source; do not just reuse the self-computed hash.
/// </summary>
public sealed record GamePackageStageRequest(string FilePath, string ExpectedPublisherSha256);

public sealed record GamePackageStageResult(
    string FileName, string StagedPath, long SizeBytes, string Sha256, string Advisory);

/// <summary>Read-only managed package inventory.</summary>
public sealed record StagedGamePackageDto(
    string Id, string FileName, long SizeBytes, DateTime LastModifiedUtc);
