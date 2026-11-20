namespace PaRuX.Contracts;

/// <summary>A configured outbound webhook. The signing secret is never part of this shape; it is shown once at creation.</summary>
public sealed class WebhookDto
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    /// <summary>Audit action prefixes this hook receives, e.g. "plugin." or "apikey.create". Empty is not allowed.</summary>
    public List<string> Events { get; set; } = new();
    public bool Enabled { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    /// <summary>HTTP status of the last delivery, 0 when it could not connect.</summary>
    public int LastStatus { get; set; }
    public int ConsecutiveFailures { get; set; }
    /// <summary>"local" for a loopback or private-network target, "internet" for anything else (needs the Internet switch).</summary>
    public string Reach { get; set; } = "local";
}

public sealed class CreateWebhookRequest
{
    public string Url { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new();
}

public sealed class WebhookCreated
{
    public WebhookDto Webhook { get; set; } = new();
    /// <summary>Signing secret for the X-PaRuX-Signature header. Shown only now; PaRuX cannot show it again.</summary>
    public string Secret { get; set; } = string.Empty;
}

public sealed class WebhookTestResult
{
    public bool Delivered { get; set; }
    public int Status { get; set; }
    public string Message { get; set; } = string.Empty;
}
