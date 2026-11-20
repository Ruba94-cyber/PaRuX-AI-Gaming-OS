using System.Net;
using System.Net.Http;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Read-only Steam achievements through the owner's own Steam Web API key and SteamID (the same data Hydra and Playnite plugins show).
/// Nothing is requested until the owner saves credentials AND the Internet switch is on; the key is stored encrypted, never returned
/// and never written to the audit log. Only api.steampowered.com is contacted, over https, without redirects.
/// </summary>
public sealed class SteamAchievementsService
{
    internal const string Host = "api.steampowered.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();
    private readonly string _stateDirectory;
    private readonly string _path;
    private readonly Func<bool> _internetEnabled;
    private readonly HttpClient _http;

    private sealed class Stored { public string SteamId { get; set; } = string.Empty; public string ProtectedKey { get; set; } = string.Empty; }

    public SteamAchievementsService(string stateDirectory, Func<bool> internetEnabled, HttpMessageHandler? handler = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _path = Path.Combine(_stateDirectory, "steam-credentials.json");
        _internetEnabled = internetEnabled;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) })
        { Timeout = TimeSpan.FromSeconds(15) };
    }

    private ISecretProtector Protector => SecretProtection.For(_stateDirectory);

    public SteamCredentialsStatus Status()
    {
        lock (Gate) { Stored? s = Read(); return new SteamCredentialsStatus { Configured = s is not null, SteamId = s?.SteamId ?? string.Empty }; }
    }

    /// <returns>Null on success, otherwise the reason.</returns>
    public string? Save(SteamCredentialsRequest request)
    {
        string key = (request?.ApiKey ?? string.Empty).Trim();
        string id = (request?.SteamId ?? string.Empty).Trim();
        if (key.Length != 32 || !key.All(Uri.IsHexDigit)) return "A Steam Web API key is 32 hexadecimal characters.";
        if (id.Length != 17 || !id.All(char.IsAsciiDigit) || !id.StartsWith("7656", StringComparison.Ordinal)) return "A SteamID64 is 17 digits and starts with 7656.";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Stored { SteamId = id, ProtectedKey = Protector.Protect("steam-api-key", key) }, Json));
                File.Move(temp, _path, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The credentials could not be saved."; }
        }
    }

    public bool Clear()
    {
        lock (Gate)
        {
            if (!File.Exists(_path)) return false;
            try { File.Delete(_path); return true; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public async Task<SteamAchievementsResult> GetAsync(int appId, CancellationToken ct = default)
    {
        var result = new SteamAchievementsResult { AppId = appId };
        if (appId <= 0) { result.Problem = "Not a valid Steam game id."; return result; }
        Stored? creds; lock (Gate) creds = Read();
        if (creds is null) { result.Problem = "Save your Steam Web API key and SteamID first."; return result; }
        if (!_internetEnabled()) { result.Problem = "Turn the Internet switch on to read Steam achievements."; return result; }
        string? key = Protector.Unprotect("steam-api-key", creds.ProtectedKey);
        if (key is null) { result.Problem = "The saved Steam key can no longer be read. Save it again."; return result; }

        try
        {
            using JsonDocument player = await GetJsonAsync(
                $"ISteamUserStats/GetPlayerAchievements/v0001/?appid={appId}&key={key}&steamid={creds.SteamId}&l=english", ct).ConfigureAwait(false);
            if (!player.RootElement.TryGetProperty("playerstats", out JsonElement stats)) { result.Problem = "Steam answered in an unexpected format."; return result; }
            if (stats.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
            {
                result.Problem = stats.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.String
                    ? Clip(err.GetString()!) : "Steam returned no achievements for this game.";
                return result;
            }
            result.GameName = stats.TryGetProperty("gameName", out JsonElement gn) && gn.ValueKind == JsonValueKind.String ? Clip(gn.GetString()!) : string.Empty;
            if (stats.TryGetProperty("achievements", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement a in list.EnumerateArray().Take(2000))
                {
                    long unlock = a.TryGetProperty("unlocktime", out JsonElement ut) && ut.TryGetInt64(out long u) ? u : 0;
                    result.Achievements.Add(new SteamAchievementDto
                    {
                        ApiName = Str(a, "apiname"), Name = Str(a, "name"), Description = Str(a, "description"),
                        Achieved = a.TryGetProperty("achieved", out JsonElement ach) && ach.ValueKind == JsonValueKind.Number && ach.GetInt32() == 1,
                        UnlockedUtc = unlock > 0 ? DateTimeOffset.FromUnixTimeSeconds(unlock).UtcDateTime : null,
                    });
                }
            result.Total = result.Achievements.Count;
            result.Unlocked = result.Achievements.Count(a => a.Achieved);
            if (result.Total == 0) result.Problem = "This game has no achievements.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        { result.Problem = "Steam refused the key or the profile is private (set Game details to Public)."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        { result.Problem = "Could not reach Steam or read its answer."; }
        return result;
    }

    /// <summary>
    /// Real Steam, always. The one exception exists so end-to-end tests can point the Core at a fake Steam on this PC: an
    /// http://127.0.0.1 or http://localhost base in <c>PARUX_STEAM_API_BASE_TEST</c>, honoured only while PARUX_DATA_ROOT is set (an
    /// isolated test profile). Anything else in the variable is ignored.
    /// </summary>
    internal static string BaseUrl()
    {
        string? over = Environment.GetEnvironmentVariable("PARUX_STEAM_API_BASE_TEST");
        if (!string.IsNullOrWhiteSpace(over)
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARUX_DATA_ROOT"))
            && Uri.TryCreate(over, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host == "127.0.0.1" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(uri.UserInfo))
            return uri.GetLeftPart(UriPartial.Authority);
        return "https://" + Host;
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl() + "/" + pathAndQuery);
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Steam answered " + (int)response.StatusCode, null, response.StatusCode);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > 2 * 1024 * 1024) throw new JsonException("Answer too large.");
        return JsonDocument.Parse(bytes);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()!) : string.Empty;

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300];

    private Stored? Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}
