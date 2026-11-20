using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WebhookServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-webhooks-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private bool _internet;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();
        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Calls) Calls.Add((request, body));
            return new HttpResponseMessage(Answer);
        }
    }

    private WebhookService NewService(Func<string, Task<IPAddress[]>>? resolve = null) =>
        new(_dir, () => _internet, _handler, resolve ?? (_ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })), (_, _, _) => { });

    private static CreateWebhookRequest Local(params string[] events) =>
        new() { Url = "http://127.0.0.1:9/hook", Events = events.ToList() };

    [Fact]
    public void Create_returns_the_secret_once_and_never_lists_it()
    {
        WebhookService service = NewService();
        WebhookCreated created = service.Create(Local("plugin."));

        Assert.StartsWith("whsec_", created.Secret);
        Assert.Equal("local", created.Webhook.Reach);
        WebhookDto listed = Assert.Single(service.List());
        Assert.Equal(created.Webhook.Id, listed.Id);
        Assert.DoesNotContain(created.Secret, File.ReadAllText(Path.Combine(_dir, "webhooks.json")));
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/x")]
    [InlineData("http://user:pw@127.0.0.1/x")]
    [InlineData("http://127.0.0.1/x#frag")]
    [InlineData("not a url")]
    [InlineData("http://example.com/x")]
    public void Unsafe_or_malformed_addresses_are_refused(string url)
    {
        Assert.Throws<InvalidDataException>(() => NewService().Create(new CreateWebhookRequest { Url = url, Events = new() { "plugin." } }));
    }

    [Fact]
    public void At_least_one_well_formed_event_is_required()
    {
        WebhookService service = NewService();
        Assert.Throws<InvalidDataException>(() => service.Create(Local()));
        Assert.Throws<InvalidDataException>(() => service.Create(Local("bad event!")));
    }

    [Fact]
    public void An_internet_address_needs_the_internet_switch_to_add_it()
    {
        WebhookService service = NewService();
        var request = new CreateWebhookRequest { Url = "https://hooks.example.com/in", Events = new() { "apikey." } };
        Assert.Throws<InvalidDataException>(() => service.Create(request));
        _internet = true;
        Assert.Equal("internet", service.Create(request).Webhook.Reach);
    }

    [Fact]
    public async Task A_delivery_is_signed_with_the_hooks_own_secret()
    {
        WebhookService service = NewService();
        WebhookCreated created = service.Create(Local("webhook."));

        WebhookTestResult? result = await service.TestAsync(created.Webhook.Id);

        Assert.NotNull(result);
        Assert.True(result!.Delivered);
        (HttpRequestMessage request, string body) = Assert.Single(_handler.Calls);
        Assert.Equal("webhook.test", request.Headers.GetValues("X-PaRuX-Event").Single());
        string expected = "sha256=" + WebhookService.Sign(created.Secret, body);
        Assert.Equal(expected, request.Headers.GetValues("X-PaRuX-Signature").Single());
        Assert.DoesNotContain("message", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_internet_hook_is_not_called_once_the_switch_goes_off_or_when_dns_points_inside()
    {
        _internet = true;
        WebhookService service = NewService();
        WebhookCreated created = service.Create(new CreateWebhookRequest { Url = "https://hooks.example.com/in", Events = new() { "x." } });

        Assert.True((await service.TestAsync(created.Webhook.Id))!.Delivered);

        _internet = false;
        Assert.False((await service.TestAsync(created.Webhook.Id))!.Delivered);

        _internet = true;
        WebhookService rebinding = NewService(_ => Task.FromResult(new[] { IPAddress.Parse("192.168.1.10") }));
        Assert.False((await rebinding.TestAsync(created.Webhook.Id))!.Delivered);
        Assert.Single(_handler.Calls);
    }

    [Fact]
    public async Task A_hook_that_keeps_failing_switches_itself_off()
    {
        WebhookService service = NewService();
        WebhookCreated created = service.Create(Local("webhook."));
        _handler.Answer = HttpStatusCode.InternalServerError;

        for (int i = 0; i < WebhookService.MaximumFailuresBeforeDisable; i++) await service.TestAsync(created.Webhook.Id);

        WebhookDto hook = service.List().Single();
        Assert.False(hook.Enabled);
        Assert.Equal(500, hook.LastStatus);
    }

    [Fact]
    public async Task Dispatch_reaches_only_hooks_subscribed_to_that_action()
    {
        WebhookService service = NewService();
        service.Create(Local("plugin."));
        service.Create(Local("apikey.create"));

        service.Dispatch(new AuditEntry { Action = "plugin.install", Subject = "demo 1.0.0", Detail = "unsigned" });
        for (int i = 0; i < 50 && _handler.Calls.Count == 0; i++) await Task.Delay(40);
        await Task.Delay(150);

        (HttpRequestMessage request, string body) = Assert.Single(_handler.Calls);
        Assert.Equal("plugin.install", request.Headers.GetValues("X-PaRuX-Event").Single());
        Assert.Contains("demo 1.0.0", body);
    }

    [Fact]
    public void Delete_removes_the_hook_and_unknown_ids_report_false()
    {
        WebhookService service = NewService();
        string id = service.Create(Local("plugin.")).Webhook.Id;
        Assert.True(service.Delete(id));
        Assert.False(service.Delete(id));
        Assert.Empty(service.List());
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.5", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("example.com", false)]
    public void Local_targets_are_recognised_only_for_this_pc_and_private_networks(string host, bool local) =>
        Assert.Equal(local, WebhookService.IsLocalLiteral(host));
}
