using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-app GOS-09 finding (2026-11-14): the uv-based engine installs answered 503 with no code when Gaming refused
/// them, while every other hard-off child answered 409 { code: "gaming_active" }. The typed refusal must be mapped
/// before the generic InvalidOperationException catch of the route.
/// </summary>
public sealed class UvInstallGamingRefusalContractTests
{
    private static string Program()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(dir!, "src", "PaRuX.Server", "Program.cs"));
    }

    [Theory]
    [InlineData("app.MapPost(\"/api/v1/cutout/engine/install\"", "InvalidOperationException or InvalidDataException")]
    [InlineData("app.MapPost(\"/api/v1/training/engine/install\"", "catch (InvalidOperationException exception)")]
    public void Install_route_maps_the_typed_gaming_refusal_to_409_before_the_generic_catch(string route, string genericCatch)
    {
        string source = Program();
        int start = source.IndexOf(route, StringComparison.Ordinal);
        Assert.True(start > 0, route);
        string block = source.Substring(start, 1400);
        int typed = block.IndexOf("catch (UnavailableDuringGamingException gaming) { return GamingHardOff.Conflict(gaming); }", StringComparison.Ordinal);
        int generic = block.IndexOf(genericCatch, StringComparison.Ordinal);
        Assert.True(typed > 0, "typed gaming refusal is not mapped");
        Assert.True(generic > typed, "the generic catch would swallow the typed refusal first");
    }

    [Fact]
    public void The_shared_gaming_conflict_is_409_with_the_gaming_active_code()
    {
        var result = GamingHardOff.Conflict(new UnavailableDuringGamingException("synthetic"));
        Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
        string json = System.Text.Json.JsonSerializer.Serialize(((Microsoft.AspNetCore.Http.IValueHttpResult)result).Value);
        Assert.Contains("gaming_active", json, StringComparison.Ordinal);
    }
}
