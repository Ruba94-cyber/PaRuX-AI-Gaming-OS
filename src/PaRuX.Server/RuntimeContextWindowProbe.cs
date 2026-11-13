using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// SEC-F01. Serves GET /api/v1/runtime/context-window. The managed llama-server answers /props only to the per-launch
/// key that Core holds, so desktop clients (the coding agent's context budget) ask Core instead of calling the runtime.
/// The read goes through the attested, key-carrying handler; an unknown window is reported with a reason, never guessed.
/// </summary>
internal static class RuntimeContextWindowProbe
{
    private static readonly HttpClient KeyedHttp =
        new(ManagedRuntimePeers.CreateHandler()) { Timeout = TimeSpan.FromSeconds(3) };

    internal static Task<RuntimeContextWindow> ReadAsync(bool refresh, CancellationToken ct) =>
        ReadAsync(KeyedHttp, LocalRuntimeService.CurrentManagedProviderEndpoint, refresh, ct);

    internal static async Task<RuntimeContextWindow> ReadAsync(
        HttpClient http, string providerEndpoint, bool refresh, CancellationToken ct)
    {
        if (refresh) ActiveContextWindow.Invalidate();
        try
        {
            int tokens = await ActiveContextWindow.GetAsync(http, providerEndpoint, ct).ConfigureAwait(false);
            return tokens > 0
                ? new RuntimeContextWindow { ContextTokens = tokens, Observed = true }
                : new RuntimeContextWindow
                {
                    Observed = false,
                    Reason = "The managed text runtime did not report a context window (not running, still loading, or it refused the request)."
                };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or PeerAttestationException or OperationCanceledException)
        {
            return new RuntimeContextWindow
            {
                Observed = false,
                Reason = "The managed text runtime could not be reached or attested."
            };
        }
    }
}
