using System.Globalization;
using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The App-side client of GET/PUT /api/v1/models/discovery/policy, driven by a fake Core transport.</summary>
public sealed class CoreDiscoveryPolicyClientTests
{
    public CoreDiscoveryPolicyClientTests() => CultureInfo.CurrentUICulture = new CultureInfo("en");

    private sealed class FakeCore : HttpMessageHandler
    {
        public string EvaluationPolicy = ModelDiscoveryPolicyValues.EvaluationAsk;
        public string PromotionPolicy = ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing;
        public string? WarningCode;
        public HttpStatusCode? FailWith;
        public bool Unreachable;
        public readonly List<string> Requests = new();
        public readonly List<string?> Tokens = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable) throw new HttpRequestException("connection refused");
            Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Tokens.Add(request.Headers.TryGetValues("X-PaRuX-Token", out var values) ? values.Single() : null);
            if (FailWith is { } status) return new HttpResponseMessage(status);

            if (request.Method == HttpMethod.Put)
            {
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var json = System.Text.Json.JsonDocument.Parse(body);
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "evaluationPolicy", StringComparison.OrdinalIgnoreCase)) EvaluationPolicy = property.Value.GetString()!;
                    if (string.Equals(property.Name, "promotionPolicy", StringComparison.OrdinalIgnoreCase)) PromotionPolicy = property.Value.GetString()!;
                }
                WarningCode = null;
            }

            string payload = "{\"settings\":{\"schemaVersion\":1,\"evaluationPolicy\":\"" + EvaluationPolicy
                + "\",\"promotionPolicy\":\"" + PromotionPolicy + "\"},\"warningCode\":"
                + (WarningCode is null ? "null" : "\"" + WarningCode + "\"") + "}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private static CoreDiscoveryPolicyClient Client(FakeCore core) =>
        new(new HttpClient(core), _ => Task.FromResult(("http://127.0.0.1:5555/", "test-token")));

    [Fact]
    public async Task Get_maps_core_values_to_the_settings_vocabulary_and_sends_the_token()
    {
        var core = new FakeCore
        {
            EvaluationPolicy = ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable,
            PromotionPolicy = ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual
        };

        DiscoveryPolicyState state = await Client(core).GetPolicyAsync();

        Assert.Equal("auto", state.AutoEvaluate);
        Assert.Equal("never", state.PromotionPolicy);
        Assert.Equal(new[] { "GET /api/v1/models/discovery/policy" }, core.Requests);
        Assert.Equal("test-token", Assert.Single(core.Tokens));
    }

    [Fact]
    public async Task Setting_one_control_writes_through_core_and_keeps_the_other_value()
    {
        var core = new FakeCore { PromotionPolicy = ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter };
        CoreDiscoveryPolicyClient client = Client(core);

        DiscoveryPolicyState afterAuto = await client.SetAutoEvaluateAsync("auto");
        DiscoveryPolicyState afterPromotion = await client.SetPromotionPolicyAsync("never");

        Assert.Equal("auto", afterAuto.AutoEvaluate);
        Assert.Equal("clearly_better", afterAuto.PromotionPolicy);
        Assert.Equal("auto", afterPromotion.AutoEvaluate);
        Assert.Equal("never", afterPromotion.PromotionPolicy);
        Assert.Equal(ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable, core.EvaluationPolicy);
        Assert.Equal(ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual, core.PromotionPolicy);
        Assert.Equal(2, core.Requests.Count(r => r.StartsWith("PUT ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_core_warning_is_surfaced_in_the_state()
    {
        var core = new FakeCore { WarningCode = "policy_file_corrupt" };

        DiscoveryPolicyState state = await Client(core).GetPolicyAsync();

        Assert.Equal("policy_file_corrupt", state.WarningCode);
        Assert.Equal("ask", state.AutoEvaluate);
        Assert.Equal("ask", state.PromotionPolicy);
    }

    [Fact]
    public async Task An_unreachable_core_is_reported_with_a_reason_and_nothing_is_claimed_saved()
    {
        var core = new FakeCore { Unreachable = true };

        DiscoveryPolicyException failure = await Assert.ThrowsAsync<DiscoveryPolicyException>(
            () => Client(core).SetAutoEvaluateAsync("auto"));

        Assert.Equal("PaRuX Core is not reachable.", failure.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "rejected the value")]
    [InlineData(HttpStatusCode.Forbidden, "did not accept this request")]
    [InlineData(HttpStatusCode.Unauthorized, "did not accept this request")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task A_core_refusal_becomes_a_readable_failure(HttpStatusCode status, string expected)
    {
        var core = new FakeCore { FailWith = status };

        DiscoveryPolicyException failure = await Assert.ThrowsAsync<DiscoveryPolicyException>(
            () => Client(core).SetPromotionPolicyAsync("never"));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_request_surfaces_as_cancellation_not_as_a_core_failure()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(new FakeCore()).GetPolicyAsync(cancelled.Token));
    }

    [Theory]
    [InlineData("ask", ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing)]
    [InlineData("clearly_better", ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter)]
    [InlineData("never", ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual)]
    [InlineData("garbage", ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing)]
    public void Promotion_mapping_round_trips_and_falls_back_to_ask(string app, string core)
    {
        Assert.Equal(core, CoreDiscoveryPolicyClient.ToCorePromotion(app));
        Assert.Equal(DiscoveryPolicyPresenter.NormalizePromotion(app), CoreDiscoveryPolicyClient.FromCorePromotion(core));
    }

    [Fact]
    public void Production_default_is_not_the_stub()
    {
        // Settings must talk to PaRuX Core unless a test installs the stub explicitly. The App-only holder is not
        // linked here, so assert the contract at the source level.
        string source = File.ReadAllText(FindRepoFile("src/PaRuX.App/Services/DiscoveryPolicyClient.cs"));
        Assert.Contains("new CoreDiscoveryPolicyClient(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new StubDiscoveryPolicyClient", source, StringComparison.Ordinal);

        string presenter = File.ReadAllText(FindRepoFile("src/PaRuX.App/Services/DiscoveryPolicyPresenter.cs"));
        Assert.DoesNotContain("session only", presenter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not available yet", presenter, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoFile(string relative)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = System.IO.Path.Combine(directory, relative);
            if (File.Exists(candidate)) return candidate;
            directory = System.IO.Path.GetDirectoryName(directory);
        }
        throw new FileNotFoundException(relative);
    }
}
