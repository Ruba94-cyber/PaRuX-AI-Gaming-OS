using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Hermetic HTTP admission tests. No real games or model inference.</summary>
public sealed class LocalGameGuideEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public LocalGameGuideEndpointTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Unauthenticated_paired_or_remote_client_cannot_prepare_local_game_guide()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/gaming/assistant/prepare",
            new LocalGameGuideRequest("synthetic", "What is next?", new[] { "private-guide" }, true));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Explicit_consent_and_nonempty_source_scope_are_required()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token",
            _factory.Services.GetRequiredService<ServerState>().Token);

        using var missingConsent = await client.PostAsJsonAsync("/api/v1/gaming/assistant/prepare",
            new LocalGameGuideRequest("synthetic", "Where next?", new[] { "guide" }, false));
        Assert.Equal(HttpStatusCode.BadRequest, missingConsent.StatusCode);

        using var empty = await client.PostAsJsonAsync("/api/v1/gaming/assistant/prepare",
            new LocalGameGuideRequest("synthetic", "Where next?", Array.Empty<string>(), true));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var tooMany = await client.PostAsJsonAsync("/api/v1/gaming/assistant/prepare",
            new LocalGameGuideRequest("synthetic", "Where next?",
                Enumerable.Range(0, 65).Select(i => "guide-" + i).ToArray(), true));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public void Host_source_policy_rejects_a_claimed_index_id_not_in_the_current_authorized_set()
    {
        var denied = LocalGameChatPolicy.Build("Synthetic Adventure", "Where is the key?",
            true, new[] { "allowed", "other" }, new[] { "allowed" });
        Assert.False(denied.Ready);
        Assert.Null(denied.Documents);

        var allowed = LocalGameChatPolicy.Build("Synthetic Adventure", "Where is the key?",
            true, new[] { "allowed" }, new[] { "allowed", "unselected" });
        Assert.True(allowed.Ready);
        Assert.Equal(new[] { "allowed" }, allowed.Documents!.SourceIds);
    }
}
