using System.Net;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SteamAchievementsServiceTests : IDisposable
{
    private const string Key = "0123456789ABCDEF0123456789ABCDEF";
    private const string SteamId = "76561198000000000";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-steam-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private bool _internet = true;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private SteamAchievementsService NewService() => new(_dir, () => _internet, _handler);

    private static SteamCredentialsRequest Good() => new() { ApiKey = Key, SteamId = SteamId };

    [Theory]
    [InlineData("short", SteamId)]
    [InlineData(Key, "123")]
    [InlineData(Key, "12345678901234567")]
    [InlineData("ZZZZ456789ABCDEF0123456789ABCDEF", SteamId)]
    public void Malformed_credentials_are_refused_and_nothing_is_written(string key, string id)
    {
        SteamAchievementsService service = NewService();
        Assert.NotNull(service.Save(new SteamCredentialsRequest { ApiKey = key, SteamId = id }));
        Assert.False(service.Status().Configured);
        Assert.False(File.Exists(Path.Combine(_dir, "steam-credentials.json")));
    }

    [Fact]
    public void The_key_is_stored_encrypted_and_never_reported()
    {
        SteamAchievementsService service = NewService();
        Assert.Null(service.Save(Good()));

        SteamCredentialsStatus status = service.Status();
        Assert.True(status.Configured);
        Assert.Equal(SteamId, status.SteamId);
        Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(_dir, "steam-credentials.json")), StringComparison.OrdinalIgnoreCase);
        Assert.True(service.Clear());
        Assert.False(service.Status().Configured);
    }

    [Fact]
    public async Task Nothing_is_requested_without_credentials_or_with_the_internet_off()
    {
        SteamAchievementsService service = NewService();
        Assert.NotEmpty((await service.GetAsync(440)).Problem);
        service.Save(Good());
        _internet = false;
        SteamAchievementsResult offline = await service.GetAsync(440);
        Assert.Contains("Internet", offline.Problem);
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public async Task Achievements_are_read_from_steam_over_https_with_the_saved_key()
    {
        SteamAchievementsService service = NewService();
        service.Save(Good());
        _handler.Body = """
        {"playerstats":{"steamID":"76561198000000000","gameName":"Demo Game","success":true,"achievements":[
          {"apiname":"A1","achieved":1,"unlocktime":1700000000,"name":"First","description":"Do it"},
          {"apiname":"A2","achieved":0,"unlocktime":0,"name":"Second","description":""}]}}
        """;

        SteamAchievementsResult result = await service.GetAsync(440);

        Assert.Equal(string.Empty, result.Problem);
        Assert.Equal("Demo Game", result.GameName);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Unlocked);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), result.Achievements[0].UnlockedUtc);
        Uri call = Assert.Single(_handler.Calls);
        Assert.Equal("https", call.Scheme);
        Assert.Equal(SteamAchievementsService.Host, call.Host);
        Assert.Contains("appid=440", call.Query);
    }

    [Fact]
    public async Task A_refusal_or_a_private_profile_becomes_a_plain_reason_without_the_key()
    {
        SteamAchievementsService service = NewService();
        service.Save(Good());
        _handler.Status = HttpStatusCode.Forbidden;
        SteamAchievementsResult denied = await service.GetAsync(440);
        Assert.Contains("private", denied.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, denied.Problem, StringComparison.OrdinalIgnoreCase);

        _handler.Status = HttpStatusCode.OK;
        _handler.Body = """{"playerstats":{"success":false,"error":"Profile is not public"}}""";
        Assert.Equal("Profile is not public", (await service.GetAsync(440)).Problem);

        _handler.Body = "not json";
        Assert.NotEmpty((await service.GetAsync(440)).Problem);
    }

    [Fact]
    public async Task An_invalid_game_id_is_refused_before_any_request()
    {
        SteamAchievementsService service = NewService();
        service.Save(Good());
        Assert.NotEmpty((await service.GetAsync(0)).Problem);
        Assert.NotEmpty((await service.GetAsync(-5)).Problem);
        Assert.Empty(_handler.Calls);
    }
}
