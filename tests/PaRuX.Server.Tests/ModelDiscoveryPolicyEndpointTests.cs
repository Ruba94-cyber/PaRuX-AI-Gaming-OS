using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// GET/PUT /api/v1/models/discovery/policy as the desktop App calls it: authenticated, validated, persisted in the
/// Core state directory and read back by the Core store that the discovery code paths use.
/// </summary>
public sealed class ModelDiscoveryPolicyEndpointTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private const string Path = "/api/v1/models/discovery/policy";

    private readonly string _stateDirectory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "parux-discovery-policy-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ModelDiscoveryPolicyEndpointTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ModelDiscoveryPolicyStore>();
            services.AddSingleton(_ => new ModelDiscoveryPolicyStore(_stateDirectory));
        }));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true); } catch (IOException) { }
    }

    private HttpClient Desktop()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Both_routes_refuse_a_request_without_the_desktop_token()
    {
        using HttpClient anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Path, new ModelDiscoveryPolicySettings())).StatusCode);
    }

    [Fact]
    public async Task Get_returns_the_ask_defaults_when_nothing_was_saved()
    {
        using HttpClient desktop = Desktop();

        ModelDiscoveryPolicyLoadResult? result = await desktop.GetFromJsonAsync<ModelDiscoveryPolicyLoadResult>(Path);

        Assert.NotNull(result);
        Assert.Null(result!.WarningCode);
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAsk, result.Settings.EvaluationPolicy);
        Assert.Equal(ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing, result.Settings.PromotionPolicy);
    }

    [Fact]
    public async Task Put_persists_the_policy_and_the_core_store_reads_it_back()
    {
        using HttpClient desktop = Desktop();
        var wanted = new ModelDiscoveryPolicySettings
        {
            EvaluationPolicy = ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            PromotionPolicy = ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual
        };

        HttpResponseMessage put = await desktop.PutAsJsonAsync(Path, wanted);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        ModelDiscoveryPolicyLoadResult? echoed = await put.Content.ReadFromJsonAsync<ModelDiscoveryPolicyLoadResult>();
        Assert.Equal(wanted.EvaluationPolicy, echoed!.Settings.EvaluationPolicy);
        ModelDiscoveryPolicyLoadResult? reread = await desktop.GetFromJsonAsync<ModelDiscoveryPolicyLoadResult>(Path);
        Assert.Equal(wanted.PromotionPolicy, reread!.Settings.PromotionPolicy);

        // A second store instance on the same state directory (as after a Core restart) sees the same value.
        ModelDiscoveryPolicyLoadResult restarted = new ModelDiscoveryPolicyStore(_stateDirectory).Load();
        Assert.Equal(wanted.EvaluationPolicy, restarted.Settings.EvaluationPolicy);
        Assert.Equal(wanted.PromotionPolicy, restarted.Settings.PromotionPolicy);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"evaluationPolicy\":\"sometimes\",\"promotionPolicy\":\"ask_before_replacing\"}")]
    [InlineData("{\"schemaVersion\":1,\"evaluationPolicy\":\"ask\",\"promotionPolicy\":\"yolo\"}")]
    [InlineData("{\"schemaVersion\":2,\"evaluationPolicy\":\"ask\",\"promotionPolicy\":\"ask_before_replacing\"}")]
    [InlineData("{\"schemaVersion\":1,\"evaluationPolicy\":null,\"promotionPolicy\":\"ask_before_replacing\"}")]
    public async Task Put_rejects_unknown_values_and_keeps_the_previous_policy(string body)
    {
        using HttpClient desktop = Desktop();
        var original = new ModelDiscoveryPolicySettings { PromotionPolicy = ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter };
        Assert.Equal(HttpStatusCode.OK, (await desktop.PutAsJsonAsync(Path, original)).StatusCode);

        HttpResponseMessage response = await desktop.PutAsync(Path, Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ModelDiscoveryPolicyLoadResult? current = await desktop.GetFromJsonAsync<ModelDiscoveryPolicyLoadResult>(Path);
        Assert.Equal(ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter, current!.Settings.PromotionPolicy);
    }

    [Fact]
    public async Task A_corrupt_policy_file_reads_as_safe_defaults_with_a_warning_and_can_be_replaced()
    {
        Directory.CreateDirectory(_stateDirectory);
        await File.WriteAllTextAsync(System.IO.Path.Combine(_stateDirectory, ModelDiscoveryPolicyStore.FileName), "{not-json");
        using HttpClient desktop = Desktop();

        ModelDiscoveryPolicyLoadResult? damaged = await desktop.GetFromJsonAsync<ModelDiscoveryPolicyLoadResult>(Path);
        Assert.Equal("policy_file_corrupt", damaged!.WarningCode);
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAsk, damaged.Settings.EvaluationPolicy);

        var repaired = new ModelDiscoveryPolicySettings { EvaluationPolicy = ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable };
        Assert.Equal(HttpStatusCode.OK, (await desktop.PutAsJsonAsync(Path, repaired)).StatusCode);
        ModelDiscoveryPolicyLoadResult? after = await desktop.GetFromJsonAsync<ModelDiscoveryPolicyLoadResult>(Path);
        Assert.Null(after!.WarningCode);
        Assert.Equal(repaired.EvaluationPolicy, after.Settings.EvaluationPolicy);
    }
}
