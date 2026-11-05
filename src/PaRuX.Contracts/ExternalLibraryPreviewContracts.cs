namespace PaRuX.Contracts;

/// <summary>
/// An explicitly pasted or locally selected export's text and declared provider.
/// The Core processes this in memory only; it never opens a path from JSON.
/// </summary>
public sealed record ExternalLibraryPreviewRequest(string? Provider, string? Json);

/// <summary>Safe title/identifier projection; executable paths and launch data are excluded.</summary>
public sealed record ExternalLibraryGamePreviewDto(string Provider, string ExternalId, string Title);

/// <summary>
/// Second, distinct owner action after viewing a preview. Imports metadata only:
/// paths, executables, save files and launch arguments in the JSON are discarded.
/// </summary>
public sealed record ExternalLibraryImportRequest(string? Provider, string? Json, bool Consent);

public sealed record ExternalLibraryImportResultDto(int Added, int AlreadyPresent, int Total);
