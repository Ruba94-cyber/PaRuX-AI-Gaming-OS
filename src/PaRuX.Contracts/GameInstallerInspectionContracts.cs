namespace PaRuX.Contracts;

/// <summary>Local, read-only installer preflight; never an execution permission.</summary>
public sealed record GameInstallerInspectionRequest(string FilePath);

public sealed record GameInstallerInspectionResult(
    string FileName,
    long SizeBytes,
    string Sha256,
    string Advisory);
