namespace PaRuX.Contracts;

public sealed record PlayniteImportRequest(string? Folder, IReadOnlyList<string>? GameIds, bool Consent);

public sealed record PlayniteGamePreviewDto(
    string Id, string Name, string Platform, string Kind, bool Importable, string SkipReason, bool HasCover, long PlaySeconds, bool Favorite);

public sealed record PlayniteImportedGameDto(string Id, string Name, string CoverPath, string TitleKey);

public sealed record PlayniteImportResultDto(
    int Imported, int AlreadyInLibrary, int Skipped, IReadOnlyList<PlayniteImportedGameDto> Games, IReadOnlyList<string> Problems);
