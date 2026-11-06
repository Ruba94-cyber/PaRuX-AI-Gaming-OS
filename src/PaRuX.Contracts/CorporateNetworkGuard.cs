namespace PaRuX.Contracts;

/// <summary>
/// Corporate Lite must make no outbound connection. Besides refusing downloads, this points every default-proxy HTTP client of the
/// process (and its children) at a dead loopback port, so a stray model-catalog or update lookup fails closed instead of reaching
/// the internet. Loopback stays direct (the Core, llama-server). Found by measurement: PaRuX.Server held connections to huggingface.co.
/// </summary>
public static class CorporateNetworkGuard
{
    public const string DeadProxy = "http://127.0.0.1:9";

    /// <summary>The OS/IT-selected proxy as it existed BEFORE PaRuX disabled generic outbound HTTP.
    /// Model-only consented traffic uses this proxy instead of bypassing enterprise settings.
    /// Null outside an explicitly approved Corporate model-download session.</summary>
    public static System.Net.IWebProxy? ApprovedModelProxy { get; private set; }

    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>
    {
        ["HTTP_PROXY"] = DeadProxy, ["HTTPS_PROXY"] = DeadProxy, ["ALL_PROXY"] = DeadProxy,
        ["NO_PROXY"] = "127.0.0.1,localhost,::1"
    };

    /// <summary>Applies the guard when Corporate Lite is active. Call before the first HttpClient is created.</summary>
    public static bool ApplyIfCorporate()
    {
        if (!CorporateMode.IsActive) return false;
        // Capture the existing system/enterprise proxy BEFORE redirecting other clients to
        // dead loopback. The model transfer client alone can use the originally configured proxy.
        if (CorporateMode.ModelDownloadsAllowed)
            ApprovedModelProxy ??= HttpClient.DefaultProxy;
        foreach (var kv in Variables) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        return true;
    }
}
