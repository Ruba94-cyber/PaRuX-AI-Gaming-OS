using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming.Emulators;

public sealed record EmulatorScanRequest(string EmulatorId, string Folder);

public sealed record EmulatorImportRequest(
    string EmulatorId, string ExecutablePath, List<string> GamePaths, bool Fullscreen, string? ExtraArguments,
    string? CorePath, string? LaunchMode = null, string? InputProfile = null);

public sealed record EmulatorImportResult(int Added, int Skipped, IReadOnlyList<string> Problems);

public static class EmulatorEndpoints
{
    public const int MaxImportBatch = 200;

    public static void MapEmulatorEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/emulators", (LocalRuntimeService runtime, CancellationToken ct) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Emulator disk discovery is disabled while Gaming." }) as IResult;
            // An emulator search that began before Gaming must also stop as
            // soon as the hard-off marker appears, not just at admission time.
            using var hardOff = new GamingHardOffWatch(() => runtime.GamingMode, ct);
            try
            {
                var detected = EmulatorDiscovery.Detect(EmulatorDiscovery.DefaultSearchRoots(), hardOff.Token);
                if (hardOff.Triggered || runtime.GamingMode)
                    return Results.Conflict(new { error = "Emulator disk discovery stopped because Gaming started." }) as IResult;
                return Results.Ok(new
                {
                    emulators = EmulatorRegistry.Builtin.Select(d => new
                    {
                        d.Id, d.Name, d.Platform, extensions = d.GameExtensions, d.RequiresCore, d.TemplateStatus,
                        fullscreenSupported = d.FullscreenArguments is { Count: > 0 },
                        windowedSupported = d.WindowedArguments is { Count: > 0 },
                    }),
                    detected,
                });
            }
            catch (OperationCanceledException) when (hardOff.Triggered)
            {
                return Results.Conflict(new { error = "Emulator disk discovery stopped because Gaming started." }) as IResult;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/emulators/scan", (
            EmulatorScanRequest request, LocalRuntimeService runtime, CancellationToken ct) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Emulator folder scanning is disabled while Gaming." }) as IResult;
            EmulatorDefinition? definition = EmulatorRegistry.Find(request.EmulatorId);
            if (definition is null) return Results.BadRequest(new { error = "Unknown emulator." });
            using var hardOff = new GamingHardOffWatch(() => runtime.GamingMode, ct);
            try
            {
                var scan = EmulatorDiscovery.Scan(definition, request.Folder, hardOff.Token);
                if (hardOff.Triggered || runtime.GamingMode)
                    return Results.Conflict(new { error = "Emulator folder scanning stopped because Gaming started." }) as IResult;
                return Results.Ok(scan);
            }
            catch (OperationCanceledException) when (hardOff.Triggered)
            {
                return Results.Conflict(new { error = "Emulator folder scanning stopped because Gaming started." }) as IResult;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/emulators/import", (
            EmulatorImportRequest request, ManualGameLibrary library, GameTitleReadCache cache,
            LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Emulator import is unavailable while Gaming." }) as IResult;
            EmulatorImportResult result = Import(request, library, () => runtime.GamingMode);
            if (result.Added > 0) cache.Invalidate();
            // If Gaming began mid-batch, preserve a truthful partial result; do
            // not report a completed import while the GPU/disk hard-off is active.
            if (runtime.GamingMode) return Results.Conflict(result) as IResult;
            return result.Problems.Count > 0 && result.Added == 0 && result.Skipped == 0
                ? Results.BadRequest(result) : Results.Ok(result);
        }).RequireDesktopOwner();
    }

    internal static EmulatorImportResult Import(EmulatorImportRequest request, ManualGameLibrary library,
        Func<bool>? gamingActive = null)
    {
        var problems = new List<string>();
        if (gamingActive?.Invoke() == true)
            return new(0, 0, new[] { "Gaming is active; emulator import was not started." });
        EmulatorDefinition? definition = EmulatorRegistry.Find(request.EmulatorId);
        if (definition is null) return new(0, 0, new[] { "Unknown emulator." });

        // Only the emulator the definition names may be registered: this is not a way to add an arbitrary program.
        string exe = request.ExecutablePath ?? string.Empty;
        if (!definition.ExecutableNames.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase) ||
            !CustomExecutableInputProvider.IsSafeHelperPath(exe))
            return new(0, 0, new[] { $"The executable must be a local, regular {definition.Name} program file ({string.Join(" / ", definition.ExecutableNames)})." });
        if (request.GamePaths is not { Count: > 0 } || request.GamePaths.Count > MaxImportBatch)
            return new(0, 0, new[] { $"Select between 1 and {MaxImportBatch} games." });
        // Libretro cores are executable third-party code. They must be regular,
        // non-linked local .dll files, not arbitrary UNC, device or junction paths.
        if (definition.RequiresCore && !GamingLocalFileSafetyPolicy.IsSafeRegularFile(
                request.CorePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll" }))
            return new(0, 0, new[] { "Select an existing regular local .dll core for this emulator." });

        string workingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var allowedExtensions = new HashSet<string>(definition.GameExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (string name in definition.GameFileNames) allowedExtensions.Add(Path.GetExtension(name));

        int added = 0, skipped = 0;
        foreach (string game in request.GamePaths)
        {
            // Multi-game import writes one entry at a time. Stop before touching
            // the next file if Gaming starts; already persisted entries remain.
            if (gamingActive?.Invoke() == true)
            {
                problems.Add("Gaming started during import. Previously added games were preserved; remaining games were not processed.");
                break;
            }
            string label = Path.GetFileName(game ?? string.Empty);
            if (!GamingLocalFileSafetyPolicy.IsSafeRegularFile(game, allowedExtensions) || !EmulatorRegistry.IsGameFile(definition, game!))
            { problems.Add($"{label}: not a supported local game file."); continue; }

            EmulatorLaunchPlan plan = EmulatorCommandBuilder.Build(definition, game!, request.Fullscreen, request.ExtraArguments,
                request.CorePath, request.LaunchMode, request.InputProfile);
            if (plan.Error is not null) { problems.Add($"{label}: {plan.Error}"); continue; }

            string title = definition.GameFileNames.Contains(label, StringComparer.OrdinalIgnoreCase)
                ? EmulatorDiscovery.TitleForGenericFileName(game!)
                : GameFolderInspector.CleanTitle(Path.GetFileNameWithoutExtension(game!));
            try
            {
                library.Add(new AddManualGameRequest(title, Path.GetFullPath(exe), plan.Arguments, workingDirectory,
                    ManualEntryKinds.Game, definition.Platform, definition.Id, Path.GetFullPath(game!)));
                added++;
                if (gamingActive?.Invoke() == true)
                {
                    problems.Add("Gaming started during import. Previously added games were preserved; remaining games were not processed.");
                    break;
                }
            }
            catch (InvalidOperationException) { skipped++; } // already in the library
            catch (ArgumentException ex) { problems.Add($"{label}: {ex.Message}"); }
        }
        return new(added, skipped, problems);
    }
}
