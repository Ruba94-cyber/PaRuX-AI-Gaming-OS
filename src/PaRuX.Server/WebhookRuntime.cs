namespace PaRuX.Server;

/// <summary>
/// The process-wide webhook service used by the HTTP routes. It reads the Internet switch live and listens to the default audit
/// log, so every audit entry reaches the hooks subscribed to it.
/// </summary>
internal static class WebhookRuntime
{
    private static readonly Lazy<WebhookService> Instance = new(() =>
    {
        var internet = new InternetSettingsService();
        var service = new WebhookService(ServerState.StateDirectory, () => internet.GetEnabled());
        AuditLogService.Appended += service.Dispatch;
        return service;
    });

    public static WebhookService Service => Instance.Value;
}
