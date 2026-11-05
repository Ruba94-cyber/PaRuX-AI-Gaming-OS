using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Discover;

public sealed class SteamStoreOptions
{
    public const string StoreHost = "store.steampowered.com";
    public int MaxResponseBytes { get; init; } = 1_048_576;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Minimum spacing between two network requests (Steam throttles appdetails around 200 per 5 minutes).</summary>
    public TimeSpan MinRequestInterval { get; init; } = TimeSpan.FromMilliseconds(1200);
    public TimeSpan DetailTtl { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan MissingTtl { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan FeaturedTtl { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan SearchTtl { get; init; } = TimeSpan.FromHours(1);
    public int MaxCacheFiles { get; init; } = 2000;
    public int MaxFeatured { get; init; } = 40;
}

/// <summary>
/// Official public Steam Store web endpoints only: appdetails, featuredcategories, storesearch. HTTPS to
/// store.steampowered.com exclusively (redirects are never followed), no API key, no login, no cookies.
/// Sent out: the numeric app id being queried (appdetails) or the user's explicit search text. The gate is
/// consulted before every request, so disabling Discover or starting a game stops traffic immediately.
/// </summary>
public sealed class SteamStoreCatalogProvider : IGameCatalogProvider
{
    private const int MaxTitleLength = 200;
    private const int MaxTerms = 24;
    private const int MaxSearchLength = 80;

    private readonly HttpClient _http;
    private readonly GameDiscoverGate _gate;
    private readonly string _cacheDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SteamStoreOptions _options;
    private readonly SemaphoreSlim _network = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public SteamStoreCatalogProvider(
        HttpClient http, GameDiscoverGate gate, string cacheDirectory,
        SteamStoreOptions? options = null, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _gate = gate;
        _cacheDir = Path.GetFullPath(cacheDirectory);
        _options = options ?? new SteamStoreOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public string Id => "steam";
    public string DisplayName => "Steam";

    /// <summary>A client that never follows redirects and carries no cookies or credentials.</summary>
    public static HttpClient CreateDefaultHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = true })
        { Timeout = TimeSpan.FromSeconds(30) };

    public static bool IsValidAppId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 10 && id.All(c => c is >= '0' and <= '9') && id[0] != '0';

    public static string StorePageUrl(string appId) => $"https://{SteamStoreOptions.StoreHost}/app/{appId}/";

    // ---- public API -------------------------------------------------------------------------------------

    public async Task<CatalogResult<GameCatalogEntryDto>> DetailAsync(string storeId, CancellationToken ct)
    {
        if (!IsValidAppId(storeId)) return CatalogResult<GameCatalogEntryDto>.Fail(GameDiscoverStatus.Failed, "Invalid Steam app id.");
        string? blocked = _gate.Check();
        if (blocked is not null) return CatalogResult<GameCatalogEntryDto>.Fail(blocked);

        string key = "detail:" + storeId;
        if (ReadCache(key, out string? cached, out bool fresh, _options.DetailTtl, _options.MissingTtl) && fresh)
        {
            if (cached == "null") return CatalogResult<GameCatalogEntryDto>.Fail(GameDiscoverStatus.Failed, "Not available in the Steam catalogue.");
            var hit = JsonSerializer.Deserialize<GameCatalogEntryDto>(cached!);
            if (hit is not null) return CatalogResult<GameCatalogEntryDto>.Ok(hit);
        }

        var uri = new Uri($"https://{SteamStoreOptions.StoreHost}/api/appdetails?appids={storeId}&l=english&cc=us");
        var fetched = await FetchAsync(uri, ct);
        if (!fetched.IsOk) return CatalogResult<GameCatalogEntryDto>.Fail(fetched.Status, fetched.Message);

        GameCatalogEntryDto? entry = ParseAppDetails(fetched.Value!, storeId);
        if (entry is null)
        {
            WriteCache(key, "null");
            return CatalogResult<GameCatalogEntryDto>.Fail(GameDiscoverStatus.Failed, "Not available in the Steam catalogue.");
        }
        WriteCache(key, JsonSerializer.Serialize(entry));
        return CatalogResult<GameCatalogEntryDto>.Ok(entry);
    }

    /// <summary>
    /// One explicitly requested Steam storefront price. Shares the existing rate-limit,
    /// cookie-free HTTPS client, host allow-list, timeout and Game Discover opt-in gate.
    /// The country is the requested Steam storefront (cc), NOT proof of the account's region.
    /// No caching, scheduler, alert, storefront login or purchase is performed here.
    /// The storefront JSON endpoint is undocumented and may stop returning prices.
    /// </summary>
    public async Task<CatalogResult<PublicStorePriceSample>> QuotePriceAsync(
        string appId, string requestedCountryCode, CancellationToken ct)
    {
        if (!IsValidAppId(appId))
            return CatalogResult<PublicStorePriceSample>.Fail(GameDiscoverStatus.Failed, "Invalid Steam app id.");
        if (string.IsNullOrWhiteSpace(requestedCountryCode) || requestedCountryCode.Length != 2
            || requestedCountryCode.Any(c => !char.IsAsciiLetter(c)))
            return CatalogResult<PublicStorePriceSample>.Fail(GameDiscoverStatus.Failed, "Invalid storefront country.");
        string? blocked = _gate.Check();
        if (blocked is not null) return CatalogResult<PublicStorePriceSample>.Fail(blocked);

        string country = requestedCountryCode.ToLowerInvariant();
        var uri = new Uri($"https://{SteamStoreOptions.StoreHost}/api/appdetails?appids={appId}&cc={country}&filters=price_overview");
        CatalogResult<string> result = await FetchAsync(uri, ct).ConfigureAwait(false);
        if (!result.IsOk)
            return CatalogResult<PublicStorePriceSample>.Fail(result.Status, result.Message);

        // If Gaming or consent changed during the network request, discard its result.
        blocked = _gate.Check();
        if (blocked is not null) return CatalogResult<PublicStorePriceSample>.Fail(blocked);
        PublicStorePriceSample? sample = PublicStorePriceWatchPolicy.ParseSteamAppDetails(
            appId, result.Value, optIn: true, observedAtUtc: _clock(), requestCountryCode: country);
        return sample is null
            ? CatalogResult<PublicStorePriceSample>.Fail(GameDiscoverStatus.Failed,
                "The Steam storefront did not return a valid price for the requested country.")
            : CatalogResult<PublicStorePriceSample>.Ok(sample);
    }

    public async Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> FeaturedAsync(CancellationToken ct)
    {
        string? blocked = _gate.Check();
        if (blocked is not null) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(blocked);

        const string key = "featured";
        if (ReadCache(key, out string? cached, out bool fresh, _options.FeaturedTtl, _options.FeaturedTtl) && fresh)
        {
            var hit = JsonSerializer.Deserialize<List<GameCatalogEntryDto>>(cached!);
            if (hit is not null) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Ok(hit);
        }

        var uri = new Uri($"https://{SteamStoreOptions.StoreHost}/api/featuredcategories?cc=us&l=english");
        var fetched = await FetchAsync(uri, ct);
        if (!fetched.IsOk) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(fetched.Status, fetched.Message);
        var list = ParseFeatured(fetched.Value!, _options.MaxFeatured);
        if (list is null) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(GameDiscoverStatus.Failed, "Unexpected Steam response.");
        WriteCache(key, JsonSerializer.Serialize(list));
        return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Ok(list);
    }

    public async Task<CatalogResult<IReadOnlyList<GameCatalogEntryDto>>> SearchAsync(string query, CancellationToken ct)
    {
        string term = (query ?? string.Empty).Trim();
        if (term.Length == 0 || term.Length > MaxSearchLength || term.Any(char.IsControl))
            return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(GameDiscoverStatus.Failed, "Search text must be 1-80 printable characters.");
        string? blocked = _gate.Check();
        if (blocked is not null) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(blocked);

        string key = "search:" + term.ToLowerInvariant();
        if (ReadCache(key, out string? cached, out bool fresh, _options.SearchTtl, _options.SearchTtl) && fresh)
        {
            var hit = JsonSerializer.Deserialize<List<GameCatalogEntryDto>>(cached!);
            if (hit is not null) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Ok(hit);
        }

        var uri = new Uri($"https://{SteamStoreOptions.StoreHost}/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=us");
        var fetched = await FetchAsync(uri, ct);
        if (!fetched.IsOk) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(fetched.Status, fetched.Message);
        var list = ParseSearch(fetched.Value!, 20);
        if (list is null) return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Fail(GameDiscoverStatus.Failed, "Unexpected Steam response.");
        WriteCache(key, JsonSerializer.Serialize(list));
        return CatalogResult<IReadOnlyList<GameCatalogEntryDto>>.Ok(list);
    }

    // ---- network ----------------------------------------------------------------------------------------

    internal static bool IsAllowedUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.Equals(uri.Host, SteamStoreOptions.StoreHost, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private async Task<CatalogResult<string>> FetchAsync(Uri uri, CancellationToken ct)
    {
        if (!IsAllowedUri(uri)) return CatalogResult<string>.Fail(GameDiscoverStatus.Failed, "Host not allowed.");
        await _network.WaitAsync(ct);
        try
        {
            TimeSpan wait = _lastRequest + _options.MinRequestInterval - _clock();
            if (wait > TimeSpan.Zero) await _delay(wait, ct);
            // Gaming or the opt-in may have changed while queued behind the rate limit.
            string? blocked = _gate.Check();
            if (blocked is not null) return CatalogResult<string>.Fail(blocked);

            _lastRequest = _clock();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "PaRuX-Discover/1");
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                    return CatalogResult<string>.Fail(GameDiscoverStatus.Failed, $"Steam answered {(int)response.StatusCode}.");
                if (response.RequestMessage?.RequestUri is { } final && !IsAllowedUri(final))
                    return CatalogResult<string>.Fail(GameDiscoverStatus.Failed, "Host not allowed.");
                long? declared = response.Content.Headers.ContentLength;
                if (declared > _options.MaxResponseBytes)
                    return CatalogResult<string>.Fail(GameDiscoverStatus.Failed, "Steam response too large.");
                await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    if (buffer.Length + read > _options.MaxResponseBytes)
                        return CatalogResult<string>.Fail(GameDiscoverStatus.Failed, "Steam response too large.");
                    buffer.Write(chunk, 0, read);
                }
                return CatalogResult<string>.Ok(new UTF8Encoding(false, false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                string? now = _gate.Check();
                return CatalogResult<string>.Fail(now ?? GameDiscoverStatus.Failed, now is null ? "Steam did not answer in time." : null);
            }
            catch (HttpRequestException) { return CatalogResult<string>.Fail(GameDiscoverStatus.Failed, "Steam could not be reached."); }
        }
        finally { _network.Release(); }
    }

    // ---- parsing (defensive: any unexpected shape yields null / skips the item) -------------------------

    internal static GameCatalogEntryDto? ParseAppDetails(string json, string expectedId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(expectedId, out JsonElement wrapper) ||
                wrapper.ValueKind != JsonValueKind.Object ||
                !wrapper.TryGetProperty("success", out JsonElement ok) || ok.ValueKind != JsonValueKind.True ||
                !wrapper.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
                return null;
            // Only full games: DLC, demos, soundtracks, tools and hardware are not recommendations.
            if (Str(data, "type") != "game") return null;
            // A response whose id disagrees with the one we asked for is rejected.
            if (data.TryGetProperty("steam_appid", out JsonElement sid) && sid.ValueKind == JsonValueKind.Number &&
                (!sid.TryGetInt64(out long parsedId) || parsedId.ToString(CultureInfo.InvariantCulture) != expectedId)) return null;

            string? name = Clean(Str(data, "name"), MaxTitleLength);
            if (name is null) return null;
            bool free = data.TryGetProperty("is_free", out JsonElement f) && f.ValueKind == JsonValueKind.True;
            string? price = null;
            if (data.TryGetProperty("price_overview", out JsonElement po) && po.ValueKind == JsonValueKind.Object)
                price = Clean(Str(po, "final_formatted"), 40);
            if (price is null && free) price = "Free to Play";

            return new GameCatalogEntryDto(
                "steam", expectedId, name,
                Descriptions(data, "genres"), Descriptions(data, "categories"),
                price, free,
                SafeImage(Str(data, "header_image"), expectedId),
                StorePageUrl(expectedId),
                Clean(Str(data, "short_description"), 400));
        }
        catch (JsonException) { return null; }
    }

    internal static List<GameCatalogEntryDto>? ParseFeatured(string json, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var result = new List<GameCatalogEntryDto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string section in new[] { "top_sellers", "specials", "new_releases" })
            {
                if (!doc.RootElement.TryGetProperty(section, out JsonElement s) || s.ValueKind != JsonValueKind.Object ||
                    !s.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (result.Count >= max) break;
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number ||
                        !idEl.TryGetInt64(out long idNum) || idNum <= 0 || idNum > 9_999_999_999) continue;
                    string id = idNum.ToString(CultureInfo.InvariantCulture);
                    string? name = Clean(Str(item, "name"), MaxTitleLength);
                    if (name is null || !seen.Add(id)) continue;
                    result.Add(new GameCatalogEntryDto(
                        "steam", id, name, Array.Empty<string>(), Array.Empty<string>(),
                        Money(item, "final_price", "currency"), false,
                        SafeImage(Str(item, "header_image") ?? Str(item, "large_capsule_image"), id),
                        StorePageUrl(id)));
                }
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    internal static List<GameCatalogEntryDto>? ParseSearch(string json, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array) return null;
            var result = new List<GameCatalogEntryDto>();
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (result.Count >= max) break;
                if (item.ValueKind != JsonValueKind.Object || Str(item, "type") is { } t && t != "app") continue;
                if (!item.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number ||
                    !idEl.TryGetInt64(out long idNum) || idNum <= 0 || idNum > 9_999_999_999) continue;
                string id = idNum.ToString(CultureInfo.InvariantCulture);
                string? name = Clean(Str(item, "name"), MaxTitleLength);
                if (name is null) continue;
                string? price = null;
                if (item.TryGetProperty("price", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
                    price = Money(p, "final", "currency");
                result.Add(new GameCatalogEntryDto("steam", id, name, Array.Empty<string>(), Array.Empty<string>(),
                    price, false, SafeImage(null, id), StorePageUrl(id)));
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? Clean(string? s, int max)
    {
        if (s is null) return null;
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) return null;
        return s.Length > max ? s[..max] : s;
    }

    private static string? Money(JsonElement obj, string amountProp, string currencyProp)
    {
        if (!obj.TryGetProperty(amountProp, out JsonElement a) || a.ValueKind != JsonValueKind.Number || !a.TryGetInt64(out long minor) || minor <= 0) return null;
        string? cur = Clean(Str(obj, currencyProp), 3);
        return (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture) + (cur is null ? "" : " " + cur);
    }

    private static List<string> Descriptions(JsonElement data, string prop)
    {
        var list = new List<string>();
        if (data.TryGetProperty(prop, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (list.Count >= MaxTerms) break;
                string? d = Clean(Str(e, "description"), 60);
                if (d is not null && !list.Contains(d, StringComparer.OrdinalIgnoreCase)) list.Add(d);
            }
        return list;
    }

    /// <summary>Only Valve-owned static image hosts; anything else falls back to the canonical header path.</summary>
    internal static string? SafeImage(string? url, string appId)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) &&
            (u.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
             u.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase) ||
             u.Host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase)))
            return u.GetLeftPart(UriPartial.Path) + u.Query;
        return $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
    }

    // ---- on-disk cache (sanitized entries, not raw responses) -------------------------------------------

    private string CachePath(string key) =>
        Path.Combine(_cacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..32] + ".json");

    private sealed record CacheFile(DateTimeOffset FetchedUtc, string Payload);

    private bool ReadCache(string key, out string? payload, out bool fresh, TimeSpan ttl, TimeSpan missingTtl)
    {
        payload = null; fresh = false;
        try
        {
            string path = CachePath(key);
            if (!File.Exists(path) || new FileInfo(path).Length > _options.MaxResponseBytes) return false;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (file is null) return false;
            payload = file.Payload;
            TimeSpan limit = payload == "null" ? missingTtl : ttl;
            TimeSpan age = _clock() - file.FetchedUtc;
            fresh = age >= TimeSpan.Zero && age < limit;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private void WriteCache(string key, string payload)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            string path = CachePath(key);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new CacheFile(_clock(), payload)));
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cache is best-effort */ }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_cacheDir).GetFiles("*.json");
        if (files.Length <= _options.MaxCacheFiles) return;
        foreach (FileInfo f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - _options.MaxCacheFiles))
            try { f.Delete(); } catch (IOException) { }
    }
}
