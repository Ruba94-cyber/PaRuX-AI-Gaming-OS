using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Server;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>All requests use synthetic exports; no filesystem or vendor account is accessed.</summary>
public sealed class ExternalLibraryPreviewEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ExternalLibraryPreviewEndpointTests(ApiSecurityTests.PaRuXFactory factory) =>
        _factory = factory;

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token",
            _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    [Fact]
    public async Task Owner_can_preview_only_title_and_external_id_without_importing()
    {
        using var client = DesktopClient();
        const string json = "{\"games\":[{\"name\":\"Synthetic Puzzle\",\"slug\":\"example-1\"," +
            "\"path\":\"not-to-return\",\"launchArguments\":\"not-to-return\"}]}";
        using var response = await client.PostAsJsonAsync(
            "/api/v1/gaming/library/import-preview",
            new { Provider = "lutris", Json = json });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var games = await response.Content.ReadFromJsonAsync<List<ExternalLibraryGamePreviewDto>>();
        ExternalLibraryGamePreviewDto game = Assert.Single(games!);
        Assert.Equal("lutris", game.Provider);
        Assert.Equal("example-1", game.ExternalId);
        Assert.Equal("Synthetic Puzzle", game.Title);
        string responseText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not-to-return", responseText);
    }

    [Fact]
    public async Task Invalid_export_returns_generic_error_without_echoing_input()
    {
        using var client = DesktopClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/gaming/library/import-preview",
            new { Provider = "playnite", Json = "{SYNTHETIC-DO-NOT-RETURN" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("SYNTHETIC-DO-NOT-RETURN",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Large_inner_export_is_refused_before_json_document_parsing()
    {
        using var client = DesktopClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/gaming/library/import-preview",
            new { Provider = "lutris", Json = new string('x', 512001) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(new string('x', 128),
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unsupported_provider_is_bad_request_without_external_access()
    {
        using var client = DesktopClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/gaming/library/import-preview",
            new { Provider = "unknown", Json = "{\"games\":[]}" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
