using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Emulators;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// "Bring my Playnite library": preview (read-only) and an explicit import into the PaRuX manual library. The import adds
/// launch entries (programs and emulator games), the favourite/hidden flags and the recorded play time; the desktop app then
/// copies covers from the returned paths. Nothing in Playnite is changed, nothing is launched, and Gaming Mode refuses both.
/// </summary>
public static class PlayniteImportEndpoints
{
    public static void MapPlayniteImportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/library/playnite/detect", (LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Playnite import is disabled while Gaming." });
            string? folder = PlayniteLibraryReader.DefaultFolder();
            return Results.Ok(new { found = folder is not null, folder });
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library/playnite/preview", (PlayniteImportRequest request, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Playnite import is disabled while Gaming." });
            if (!TryRead(request?.Folder, out IReadOnlyList<PlayniteImportGame> games, out IResult? failure)) return failure!;
            return Results.Ok(games.Select(g => new PlayniteGamePreviewDto(
                g.Id, g.Name, g.Platform, g.Kind, g.Importable, g.SkipReason, g.CoverPath.Length > 0, g.PlaySeconds, g.Favorite)).ToArray());
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/library/playnite/import", (
            PlayniteImportRequest request, ManualGameLibrary library, GameTitleStore titles, GameTitleReadCache cache,
            LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode) return Results.Conflict(new { error = "Playnite import is disabled while Gaming." });
            if (request is null || !request.Consent)
                return Results.BadRequest(new { error = "Importing requires a separate, explicit owner confirmation." });
            if (!TryRead(request.Folder, out IReadOnlyList<PlayniteImportGame> games, out IResult? failure)) return failure!;

            HashSet<string>? wanted = request.GameIds is { Count: > 0 }
                ? new HashSet<string>(request.GameIds, StringComparer.OrdinalIgnoreCase) : null;
            int imported = 0, already = 0, skipped = 0;
            var done = new List<PlayniteImportedGameDto>();
            var problems = new List<string>();
            foreach (PlayniteImportGame game in games)
            {
                if (wanted is not null && !wanted.Contains(game.Id)) continue;
                if (!game.Importable) { skipped++; continue; }
                try
                {
                    string arguments = game.Arguments, workingDirectory = game.WorkingDirectory;
                    if (game.Kind == "emulator")
                    {
                        string? refusal = PlanEmulatorLaunch(game, out arguments, out workingDirectory);
                        if (refusal is not null) { skipped++; problems.Add(game.Name + ": " + refusal); continue; }
                    }
                    ManualGameDto added = library.Add(new AddManualGameRequest(
                        game.Name, game.ExecutablePath, arguments, workingDirectory,
                        ManualEntryKinds.Game, game.Platform, game.EmulatorId, game.GameFilePath));
                    string key = GameTitleCatalog.KeyFor(added.Name, GameTitleCatalog.InstallationId(
                        string.IsNullOrEmpty(added.EmulatorId) ? GameInstallationSources.Manual : GameInstallationSources.Emulator, added.Id));
                    if (game.Favorite) titles.SetFavorite(key, true);
                    if (game.Hidden) titles.SetHidden(key, true);
                    if (game.PlaySeconds > 0)
                    {
                        TimeSpan span = TimeSpan.FromSeconds(Math.Min(game.PlaySeconds, (long)GameTitleStore.MaxSession.TotalSeconds));
                        titles.AddSession(key, (game.LastPlayedUtc ?? DateTime.UtcNow) - span, span);
                    }
                    done.Add(new PlayniteImportedGameDto(added.Id, added.Name, game.CoverPath, key));
                    imported++;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already in the library", StringComparison.OrdinalIgnoreCase))
                { already++; }
                catch (ArgumentException ex)
                { skipped++; problems.Add(game.Name + ": " + ex.Message); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                { skipped++; problems.Add(game.Name + ": " + ex.Message); }
            }
            cache.Invalidate();
            return Results.Ok(new PlayniteImportResultDto(imported, already, skipped, done, problems));
        }).RequireDesktopOwner();
    }

    /// <summary>Same rules as the emulator import: only the emulator program the recipe names, a regular local game file, and the recipe's own launch arguments.</summary>
    private static string? PlanEmulatorLaunch(PlayniteImportGame game, out string arguments, out string workingDirectory)
    {
        arguments = "";
        workingDirectory = "";
        EmulatorDefinition? definition = EmulatorRegistry.Find(game.EmulatorId);
        if (definition is null) return "Unknown emulator.";
        if (!definition.ExecutableNames.Contains(Path.GetFileName(game.ExecutablePath), StringComparer.OrdinalIgnoreCase)
            || !CustomExecutableInputProvider.IsSafeHelperPath(game.ExecutablePath))
            return "The emulator program is missing or is not a regular local file.";
        var allowed = new HashSet<string>(definition.GameExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (string name in definition.GameFileNames) allowed.Add(Path.GetExtension(name));
        if (!GamingLocalFileSafetyPolicy.IsSafeRegularFile(game.GameFilePath, allowed) || !EmulatorRegistry.IsGameFile(definition, game.GameFilePath))
            return "The game file is missing or is not a supported local file.";
        if (definition.RequiresCore) return "This emulator needs a core file; set it up in Emulators & ROMs.";
        EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(definition, game.GameFilePath, fullscreen: false, extraArguments: null, corePath: null);
        if (plan.Error is not null) return plan.Error;
        arguments = plan.Arguments;
        workingDirectory = Path.GetDirectoryName(Path.GetFullPath(game.ExecutablePath))!;
        return null;
    }

    private static bool TryRead(string? folder, out IReadOnlyList<PlayniteImportGame> games, out IResult? failure)
    {
        games = Array.Empty<PlayniteImportGame>();
        failure = null;
        string? path = string.IsNullOrWhiteSpace(folder) ? PlayniteLibraryReader.DefaultFolder() : folder;
        if (path is null) { failure = Results.NotFound(new { error = "No Playnite library was found on this PC." }); return false; }
        try { games = PlayniteLibraryReader.Read(path); return true; }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or UnauthorizedAccessException or LiteDB.LiteException)
        {
            // Never echo the folder or database content back.
            failure = Results.BadRequest(new { error = "The Playnite library could not be read." });
            return false;
        }
    }
}
