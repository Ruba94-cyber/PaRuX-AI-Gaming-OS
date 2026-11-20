namespace PaRuX.Server;

/// <summary>
/// The process-wide webhook service used by the HTTP routes. It reads the Internet switch live and listens to the default audit
/// log, so every audit entry reaches the hooks subscribed to it.
/// </summary>
internal static class WebhookRuntime
{
    private static Func<bool>? _internetEnabled;

    /// <summary>Called once at startup with the app's own Internet switch, so webhooks and Steam read the live value instead of a private copy.</summary>
    public static void Configure(Func<bool> internetEnabled) => _internetEnabled = internetEnabled;

    private static bool InternetEnabled() => _internetEnabled?.Invoke() ?? false;

    private static readonly Lazy<WebhookService> Instance = new(() =>
    {
        var service = new WebhookService(ServerState.StateDirectory, InternetEnabled);
        AuditLogService.Appended += service.Dispatch;
        return service;
    });

    public static WebhookService Service => Instance.Value;

    private static readonly Lazy<SteamAchievementsService> Steam = new(() =>
        new SteamAchievementsService(ServerState.StateDirectory, InternetEnabled));

    public static SteamAchievementsService SteamAchievements => Steam.Value;
}
