using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PaRuX.Server.Orchestration.Gaming;

public static class GameLaunchProfileEndpoints
{
    public static void MapGameLaunchProfileEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/titles/{key}/launch-profile", (string key, GameLaunchProfileStore store) =>
        {
            if (!GameTitleEndpoints.ValidTitleKey(key)) return Results.BadRequest() as IResult;
            // No saved profile = defaults, so clients never special-case absence.
            return Results.Ok(store.Get(key) ?? new GameLaunchProfile());
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/titles/{key}/launch-profile", (string key, GameLaunchProfile profile, GameLaunchProfileStore store) =>
        {
            if (profile is null) return Results.BadRequest() as IResult;
            string? problem = store.Save(key, profile);
            if (problem is null) return Results.Ok(store.Get(key));
            return store.LoadError is not null || problem.Contains("could not be", StringComparison.Ordinal)
                ? Results.Problem(problem) : Results.BadRequest(new { error = problem });
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/titles/{key}/launch-profile", (string key, GameLaunchProfileStore store) =>
        {
            if (!GameTitleEndpoints.ValidTitleKey(key)) return Results.BadRequest() as IResult;
            return store.Delete(key) ? Results.NoContent() : Results.NotFound();
        }).RequireDesktopOwner();
    }
}
