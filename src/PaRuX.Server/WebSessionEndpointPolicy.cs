namespace PaRuX.Server;

/// <summary>
/// SEC-F05: authoritative, default-deny capability boundary for a signed-in browser session (owner "web:{id}").
/// A session is a convenience login for the single-page client that PaRuX serves, not desktop-equivalent privilege,
/// so a route is reachable from a browser only when it is listed here. Everything else, including every route added
/// later, answers 403 until someone decides otherwise. The list is exactly what <c>WebClient/index.html</c> calls.
/// The route-decision test enumerates every registered route against this class and a reviewed baseline.
/// </summary>
internal static class WebSessionEndpointPolicy
{
    internal static bool IsAllowed(string method, PathString requestPath)
    {
        string path = (requestPath.Value ?? string.Empty).TrimEnd('/');
        if (path.Length == 0) path = "/";
        method = (method ?? string.Empty).ToUpperInvariant();

        return method switch
        {
            "GET" => Exact(path, WebClientEndpoint.Path, "/api/v1/health", "/api/v1/web/session", "/api/v1/conversations",
                    "/api/v1/models/library", "/api/v1/runtime/status", "/api/v1/internet")
                || OneId(path, "/api/v1/conversations"),
            "POST" => Exact(path, "/api/v1/chat/completions", "/api/v1/models/library/unload", "/api/v1/internet")
                || LoadModelPath(path),
            "PUT" => OneId(path, "/api/v1/conversations"),
            "DELETE" => Exact(path, "/api/v1/web/session") || OneId(path, "/api/v1/conversations"),
            _ => false
        };
    }

    private static bool Exact(string path, params string[] allowed) =>
        allowed.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase));

    private static bool OneId(string path, string prefix)
    {
        if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return false;
        string id = path[(prefix.Length + 1)..];
        return id.Length is > 0 and <= 160 && id.IndexOf('/') < 0;
    }

    private static bool LoadModelPath(string path)
    {
        const string prefix = "/api/v1/models/library/";
        const string suffix = "/load";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        string name = path[prefix.Length..^suffix.Length];
        return name.Length is > 0 and <= 260 && name.IndexOf('/') < 0;
    }
}
