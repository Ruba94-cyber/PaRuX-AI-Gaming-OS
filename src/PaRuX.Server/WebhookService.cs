using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Outbound webhooks for audit events (plugin installs, pairing, API keys, Internet policy...). Off until the owner adds one.
/// Safety rules: a target on this PC or the private network is allowed as is; anything else needs the Internet switch to be ON
/// at the time of every delivery, and a host name must resolve only to public addresses (so a name cannot be pointed at the LAN).
/// Redirects are never followed, the body is small and carries no message content, every delivery is signed with the hook's own
/// secret (HMAC-SHA256), and a hook that keeps failing switches itself off. Delivery never blocks or fails the action it reports.
/// </summary>
public sealed class WebhookService
{
    internal const int MaximumHooks = 20;
    internal const int MaximumFailuresBeforeDisable = 10;
    internal const int MaximumDeliveriesPerMinute = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();
    private readonly string _path;
    private readonly string _stateDirectory;
    private readonly Func<bool> _internetEnabled;
    private readonly Func<string, Task<IPAddress[]>> _resolve;
    private readonly Action<string, string, string?> _audit;
    private readonly HttpClient _http;
    private readonly Dictionary<string, Queue<DateTime>> _recent = new();

    private sealed class StoredHook
    {
        public WebhookDto Info { get; set; } = new();
        public string ProtectedSecret { get; set; } = string.Empty;
    }

    private sealed class Document { public List<StoredHook> Hooks { get; set; } = new(); }

    public WebhookService(string stateDirectory, Func<bool> internetEnabled, HttpMessageHandler? handler = null, Func<string, Task<IPAddress[]>>? resolve = null,
        Action<string, string, string?>? audit = null)
    {
        _audit = audit ?? ((action, subject, detail) => AuditLogService.TryRecord(action, subject, detail));
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _path = Path.Combine(_stateDirectory, "webhooks.json");
        _internetEnabled = internetEnabled;
        _resolve = resolve ?? (host => Dns.GetHostAddressesAsync(host));
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
        { Timeout = TimeSpan.FromSeconds(8) };
    }

    private ISecretProtector Protector => SecretProtection.For(_stateDirectory);

    public IReadOnlyList<WebhookDto> List()
    {
        lock (Gate) return Load().Hooks.Select(h => Clone(h.Info)).ToList();
    }

    public WebhookCreated Create(CreateWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = ValidateUrl(request.Url);
        List<string> events = (request.Events ?? new()).Select(e => (e ?? string.Empty).Trim().ToLowerInvariant())
            .Where(e => e.Length > 0).Distinct().Take(20).ToList();
        if (events.Count == 0) throw new InvalidDataException("Choose at least one event, for example \"plugin.\" or \"apikey.\".");
        if (events.Any(e => e.Length > 64 || e.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))))
            throw new InvalidDataException("Event names use letters, digits, dots, dashes and underscores only.");
        string reach = IsLocalLiteral(uri.Host) ? "local" : "internet";
        if (reach == "internet" && !_internetEnabled())
            throw new InvalidDataException("This address is outside your network. Turn the Internet switch on first, or use an address on this PC or your local network.");

        lock (Gate)
        {
            Document doc = Load();
            if (doc.Hooks.Count >= MaximumHooks) throw new InvalidDataException("At most " + MaximumHooks + " webhooks.");
            string secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var info = new WebhookDto
            {
                Id = Guid.NewGuid().ToString("N")[..12], Url = uri.GetLeftPart(UriPartial.Path) + uri.Query,
                Events = events, Enabled = true, CreatedUtc = DateTime.UtcNow, Reach = reach,
            };
            doc.Hooks.Add(new StoredHook { Info = info, ProtectedSecret = Protector.Protect("webhook:" + info.Id, secret) });
            Save(doc);
            _audit("webhook.create", info.Id, reach);
            return new WebhookCreated { Webhook = Clone(info), Secret = secret };
        }
    }

    public bool Delete(string id)
    {
        lock (Gate)
        {
            Document doc = Load();
            int removed = doc.Hooks.RemoveAll(h => string.Equals(h.Info.Id, id, StringComparison.Ordinal));
            if (removed == 0) return false;
            Save(doc);
        }
        _audit("webhook.delete", id, null);
        return true;
    }

    /// <summary>Sends a harmless "webhook.test" event to one hook and reports what happened, regardless of its event list.</summary>
    public async Task<WebhookTestResult?> TestAsync(string id, CancellationToken ct = default)
    {
        StoredHook? hook;
        lock (Gate) hook = Load().Hooks.FirstOrDefault(h => string.Equals(h.Info.Id, id, StringComparison.Ordinal));
        if (hook is null) return null;
        int status = await DeliverAsync(hook, "webhook.test", "test", "Test delivery from PaRuX", ct).ConfigureAwait(false);
        return new WebhookTestResult
        {
            Delivered = status is >= 200 and < 300, Status = status,
            Message = status switch
            {
                >= 200 and < 300 => "Delivered.",
                0 => "Could not deliver: the address is unreachable, not allowed right now, or timed out.",
                _ => "The receiver answered HTTP " + status + ".",
            },
        };
    }

    /// <summary>Fire-and-forget: queues delivery to every enabled hook subscribed to this action. Never throws.</summary>
    public void Dispatch(AuditEntry entry)
    {
        List<StoredHook> targets;
        try
        {
            lock (Gate)
                targets = Load().Hooks.Where(h => h.Info.Enabled && h.Info.Events.Any(e => entry.Action.StartsWith(e, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        catch (Exception) { return; }
        foreach (StoredHook hook in targets)
            _ = Task.Run(async () =>
            {
                try { await DeliverAsync(hook, entry.Action, entry.Subject, entry.Detail, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            });
    }

    private async Task<int> DeliverAsync(StoredHook hook, string action, string subject, string detail, CancellationToken ct)
    {
        if (!AllowedNow(hook.Info.Id)) return 0;
        Uri uri = new(hook.Info.Url);
        int status = 0;
        try
        {
            if (!await TargetAllowedAsync(uri).ConfigureAwait(false)) { Record(hook.Info.Id, 0); return 0; }
            string? secret = Protector.Unprotect("webhook:" + hook.Info.Id, hook.ProtectedSecret);
            if (secret is null) { Record(hook.Info.Id, 0); return 0; }
            string body = JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid().ToString("N"), action, subject = Clip(subject), detail = Clip(detail), timestampUtc = DateTime.UtcNow,
            }, Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-PaRuX-Event", action);
            request.Headers.Add("X-PaRuX-Signature", "sha256=" + Sign(secret, body));
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or SocketException) { status = 0; }
        Record(hook.Info.Id, status);
        return status;
    }

    internal static string Sign(string secret, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static string Clip(string value) => value.Length <= 200 ? value : value[..200];

    private bool AllowedNow(string id)
    {
        lock (_recent)
        {
            if (!_recent.TryGetValue(id, out Queue<DateTime>? q)) _recent[id] = q = new Queue<DateTime>();
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count >= MaximumDeliveriesPerMinute) return false;
            q.Enqueue(DateTime.UtcNow);
            return true;
        }
    }

    /// <summary>Local and private literals pass; everything else needs the Internet switch and public-only DNS answers.</summary>
    internal async Task<bool> TargetAllowedAsync(Uri uri)
    {
        if (IsLocalLiteral(uri.Host)) return true;
        if (!_internetEnabled()) return false;
        IPAddress[] addresses = IPAddress.TryParse(uri.Host, out IPAddress? literal) ? new[] { literal } : await _resolve(uri.Host).ConfigureAwait(false);
        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    internal static bool IsLocalLiteral(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out IPAddress? ip) && (IPAddress.IsLoopback(ip) || IsPrivate(ip));
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    private static bool IsPublic(IPAddress ip) =>
        !(IPAddress.IsLoopback(ip) || IsPrivate(ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip)
          || (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] is 0 or 100 or >= 224));

    internal static Uri ValidateUrl(string? raw)
    {
        if (!Uri.TryCreate((raw ?? string.Empty).Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Use a full http or https address without a user name, password or #fragment.");
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLocalLiteral(uri.Host))
            throw new InvalidDataException("An address outside your network must use https.");
        return uri;
    }

    private void Record(string id, int status)
    {
        lock (Gate)
        {
            Document doc = Load();
            StoredHook? hook = doc.Hooks.FirstOrDefault(h => h.Info.Id == id);
            if (hook is null) return;
            hook.Info.LastAttemptUtc = DateTime.UtcNow;
            hook.Info.LastStatus = status;
            if (status is >= 200 and < 300) hook.Info.ConsecutiveFailures = 0;
            else if (++hook.Info.ConsecutiveFailures >= MaximumFailuresBeforeDisable) hook.Info.Enabled = false;
            Save(doc);
        }
    }

    private Document Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), Json) ?? new Document() : new Document(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new Document(); }
    }

    private void Save(Document doc)
    {
        Directory.CreateDirectory(_stateDirectory);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(doc, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static WebhookDto Clone(WebhookDto d) => new()
    {
        Id = d.Id, Url = d.Url, Events = d.Events.ToList(), Enabled = d.Enabled, CreatedUtc = d.CreatedUtc,
        LastAttemptUtc = d.LastAttemptUtc, LastStatus = d.LastStatus, ConsecutiveFailures = d.ConsecutiveFailures, Reach = d.Reach,
    };
}
