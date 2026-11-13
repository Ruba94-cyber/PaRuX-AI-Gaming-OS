using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>A failure to read or save the discovery policy, with a reason that is safe to show to the user.</summary>
    public sealed class DiscoveryPolicyException : Exception
    {
        public DiscoveryPolicyException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Reads and writes the candidate discovery policy through PaRuX Core
    /// (<c>GET/PUT /api/v1/models/discovery/policy</c>, desktop owner only). Core owns persistence and validation;
    /// this client only maps the Settings vocabulary (ask/auto, ask/clearly_better/never) to the Core values.
    /// The HTTP transport and the Core endpoint/token lookup are injected so the mapping and the error handling stay testable.
    /// </summary>
    public sealed class CoreDiscoveryPolicyClient : IDiscoveryPolicyClient
    {
        internal const string PolicyPath = "/api/v1/models/discovery/policy";

        private readonly HttpClient _http;
        private readonly Func<CancellationToken, Task<(string Endpoint, string Token)>> _resolveCore;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public CoreDiscoveryPolicyClient(
            HttpClient http,
            Func<CancellationToken, Task<(string Endpoint, string Token)>> resolveCore)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _resolveCore = resolveCore ?? throw new ArgumentNullException(nameof(resolveCore));
        }

        public async Task<DiscoveryPolicyState> GetPolicyAsync(CancellationToken ct = default)
        {
            ModelDiscoveryPolicyLoadResult loaded = await SendAsync(HttpMethod.Get, null, ct).ConfigureAwait(false);
            return ToState(loaded);
        }

        public Task<DiscoveryPolicyState> SetAutoEvaluateAsync(string autoEvaluate, CancellationToken ct = default) =>
            UpdateAsync(settings => settings.EvaluationPolicy = ToCoreEvaluation(autoEvaluate), ct);

        public Task<DiscoveryPolicyState> SetPromotionPolicyAsync(string promotionPolicy, CancellationToken ct = default) =>
            UpdateAsync(settings => settings.PromotionPolicy = ToCorePromotion(promotionPolicy), ct);

        // Read-modify-write so changing one control never resets the other one to a stale value.
        private async Task<DiscoveryPolicyState> UpdateAsync(Action<ModelDiscoveryPolicySettings> change, CancellationToken ct)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ModelDiscoveryPolicyLoadResult current = await SendAsync(HttpMethod.Get, null, ct).ConfigureAwait(false);
                ModelDiscoveryPolicySettings next = new()
                {
                    EvaluationPolicy = current.Settings.EvaluationPolicy,
                    PromotionPolicy = current.Settings.PromotionPolicy
                };
                change(next);
                ModelDiscoveryPolicyLoadResult saved = await SendAsync(HttpMethod.Put, next, ct).ConfigureAwait(false);
                return ToState(saved);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task<ModelDiscoveryPolicyLoadResult> SendAsync(
            HttpMethod method, ModelDiscoveryPolicySettings? body, CancellationToken ct)
        {
            try
            {
                (string endpoint, string token) = await _resolveCore(ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, endpoint.TrimEnd('/') + PolicyPath);
                request.Headers.Add("X-PaRuX-Token", token);
                if (body is not null) request.Content = JsonContent.Create(body);

                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new DiscoveryPolicyException(DescribeStatus(response.StatusCode));

                ModelDiscoveryPolicyLoadResult? result =
                    await response.Content.ReadFromJsonAsync<ModelDiscoveryPolicyLoadResult>(cancellationToken: ct).ConfigureAwait(false);
                if (result?.Settings is null)
                    throw new DiscoveryPolicyException(UiText.Pick(
                        "PaRuX Core returned an unreadable response.", "PaRuX Core ha restituito una risposta non leggibile."));
                return result;
            }
            catch (DiscoveryPolicyException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                or NotSupportedException or InvalidOperationException or System.IO.IOException)
            {
                throw new DiscoveryPolicyException(UiText.Pick(
                    "PaRuX Core is not reachable.", "PaRuX Core non è raggiungibile."), ex);
            }
        }

        private static string DescribeStatus(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => UiText.Pick(
                "PaRuX Core did not accept this request (only the desktop app can change this policy).",
                "PaRuX Core non ha accettato la richiesta (solo l'app desktop può modificare questi criteri)."),
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => UiText.Pick(
                "PaRuX Core rejected the value.", "PaRuX Core ha rifiutato il valore."),
            _ => UiText.Pick(
                "PaRuX Core returned an error (HTTP " + (int)status + ").",
                "PaRuX Core ha restituito un errore (HTTP " + (int)status + ").")
        };

        internal static DiscoveryPolicyState ToState(ModelDiscoveryPolicyLoadResult loaded) =>
            new(FromCoreEvaluation(loaded.Settings.EvaluationPolicy),
                FromCorePromotion(loaded.Settings.PromotionPolicy),
                GamingActive: false,
                WarningCode: loaded.WarningCode);

        internal static string ToCoreEvaluation(string autoEvaluate) =>
            DiscoveryPolicyPresenter.IsAutoEvaluateOn(autoEvaluate)
                ? ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable
                : ModelDiscoveryPolicyValues.EvaluationAsk;

        internal static string FromCoreEvaluation(string? core) =>
            core == ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable
                ? DiscoveryPolicyPresenter.AutoEvaluateAuto
                : DiscoveryPolicyPresenter.AutoEvaluateAsk;

        internal static string ToCorePromotion(string promotion) =>
            DiscoveryPolicyPresenter.NormalizePromotion(promotion) switch
            {
                DiscoveryPolicyPresenter.PromotionClearlyBetter => ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter,
                DiscoveryPolicyPresenter.PromotionNever => ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual,
                _ => ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing
            };

        internal static string FromCorePromotion(string? core) => core switch
        {
            ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter => DiscoveryPolicyPresenter.PromotionClearlyBetter,
            ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual => DiscoveryPolicyPresenter.PromotionNever,
            _ => DiscoveryPolicyPresenter.PromotionAsk
        };
    }
}
