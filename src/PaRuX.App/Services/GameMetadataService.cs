using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Optional covers and descriptions for the Gaming library. It runs only after the user turned it on (their game
    /// names are sent to the Steam store and Wikipedia to find the title), never while Gaming is active, never replaces
    /// a cover the user chose, and reads only the two catalogues named in <see cref="GameMetadataParsing"/>.
    /// Every attempt is remembered, so a title that no catalogue knows is not asked for again before a week passes.
    /// </summary>
    public static class GameMetadataService
    {
        private const int MaxTitlesPerRun = 40;
        private static readonly TimeSpan RetryUnknownAfter = TimeSpan.FromDays(7);
        private static readonly HttpClient Http = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public const string ConsentText =
            "Covers and info look your games up by name on the public Steam store and on Wikipedia. The game names are sent " +
            "to those services; no account, key, play time or file path is sent. Nothing is contacted while this is off or " +
            "while a game is running. A cover you chose yourself is never replaced.";

        // SteamGridDB carries the user's key, so it gets its own client that never follows a redirect (the key must not travel on).
        private static readonly HttpClient SgdbHttp = CreateSgdbClient();
        private const string SgdbApi = "https://www.steamgriddb.com/api/v2";
        private static string? _notice;

        private static HttpClient CreateSgdbClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX/2.0 (local game library; https://github.com/Ruba94-cyber)");
            return client;
        }

        /// <summary>A one-time, user-readable message about the optional SteamGridDB source (e.g. a rejected key); cleared when read.</summary>
        public static string? TakeNotice() => Interlocked.Exchange(ref _notice, null);

        private sealed class SgdbSession
        {
            public SgdbSession(string key) => Key = key;
            public string Key { get; }
            public bool Disabled { get; set; }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX/2.0 (local game library; https://github.com/Ruba94-cyber)");
            return client;
        }

        private static readonly object FileGate = new();

        private static string InfoFolder => Path.Combine(AppPaths.PaRuXRoot, "game-info");

        private static string InfoPath(string titleKey) =>
            Path.Combine(InfoFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(titleKey))).ToLowerInvariant()[..32] + ".json");

        /// <summary>The stored description of a title, or null when none was found (or nothing was fetched yet).</summary>
        public static GameInfoDto? TryGetInfo(string titleKey)
        {
            try
            {
                string path = InfoPath(titleKey);
                lock (FileGate)
                {
                    if (!File.Exists(path)) return null;
                    GameInfoDto? info = JsonSerializer.Deserialize<GameInfoDto>(File.ReadAllText(path), JsonOptions);
                    return info is { Name.Length: > 0 } ? info : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }

        private static bool ShouldAsk(string titleKey)
        {
            try
            {
                string path = InfoPath(titleKey);
                GameInfoDto? stored;
                lock (FileGate)
                {
                    if (!File.Exists(path)) return true;
                    stored = JsonSerializer.Deserialize<GameInfoDto>(File.ReadAllText(path), JsonOptions);
                    if (stored is not { Name.Length: > 0 })
                        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > RetryUnknownAfter;
                }
                return stored.CoverUrl is not null && GameCoverStore.TryGetPath(titleKey) is null; // finish a missing cover
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; }
        }

        /// <summary>Fetches missing covers/descriptions for at most 40 titles. Returns how many titles gained information.</summary>
        public static async Task<int> FetchMissingAsync(IReadOnlyList<GameTitleDto> titles, Func<CancellationToken, Task<bool>> gamingActive, CancellationToken ct)
        {
            int found = 0, asked = 0;
            string? sgdbKey = SteamGridDbKeyStore.TryGet();
            SgdbSession? sgdb = sgdbKey is null ? null : new SgdbSession(sgdbKey);
            var igdbPair = IgdbKeyStore.TryGet();
            IgdbSession? igdb = igdbPair is null ? null : new IgdbSession(igdbPair.Value.ClientId, igdbPair.Value.ClientSecret);
            foreach (GameTitleDto title in titles)
            {
                ct.ThrowIfCancellationRequested();
                if (await gamingActive(ct).ConfigureAwait(false)) break;
                if (title.Hidden || !ShouldAsk(title.TitleKey)) continue;
                if (++asked > MaxTitlesPerRun) break;
                try
                {
                    GameInfoDto? known = TryGetInfo(title.TitleKey);
                    // Already described: only the cover is missing, so retry that download without asking the catalogues again.
                    string? fallbackCover = null;
                    GameInfoDto? info = known;
                    if (known is null) (info, fallbackCover) = await LookUpAsync(title, sgdb, igdb, ct).ConfigureAwait(false);
                    if (known is null) Save(title.TitleKey, info);
                    if (info is null) continue;
                    found++;
                    if (info.CoverUrl is not null && GameCoverStore.TryGetPath(title.TitleKey) is null
                        && !await DownloadCoverAsync(title.TitleKey, info.CoverUrl, ct).ConfigureAwait(false)
                        && fallbackCover is not null)
                    {
                        // The SteamGridDB image could not be fetched: use the Steam/Wikipedia cover instead and remember that.
                        info = info with { CoverUrl = fallbackCover };
                        Save(title.TitleKey, info);
                        await DownloadCoverAsync(title.TitleKey, fallbackCover, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
                {
                    // Offline or a catalogue hiccup: leave no marker, so the next run tries this title again.
                    continue;
                }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            return found;
        }

        /// <summary>
        /// Description and genres always come from Steam/Wikipedia. When a SteamGridDB key is set, its portrait grid is tried
        /// for the cover first; the catalogue cover is returned as the fallback for when that image cannot be downloaded.
        /// </summary>
        private static async Task<(GameInfoDto? Info, string? FallbackCover)> LookUpAsync(GameTitleDto title, SgdbSession? sgdb, IgdbSession? igdb, CancellationToken ct)
        {
            (string Url, long GameId, string Name)? grid = sgdb is { Disabled: false }
                ? await LookUpSteamGridDbAsync(CleanName(title.Name), sgdb, ct).ConfigureAwait(false) : null;
            GameInfoDto? info = await LookUpCatalogueAsync(title, ct).ConfigureAwait(false);
            // IGDB only fills what is still missing: a portrait cover when SteamGridDB found none, or the whole entry when no catalogue knew the game.
            GameInfoDto? igdbInfo = igdb is { Disabled: false } && (grid is null || info is null)
                ? await LookUpIgdbAsync(title, CleanName(title.Name), igdb, ct).ConfigureAwait(false) : null;
            if (grid is not { } g)
            {
                if (igdbInfo is null) return (info, null);
                if (info is null) return (igdbInfo, null);
                return igdbInfo.CoverUrl is null ? (info, null) : (info with { CoverUrl = igdbInfo.CoverUrl }, info.CoverUrl);
            }
            if (info is null)
            {
                if (igdbInfo is not null) return (igdbInfo with { CoverUrl = g.Url }, igdbInfo.CoverUrl);
                return (new GameInfoDto(title.TitleKey, g.Name.Length > 120 ? g.Name[..120] : g.Name, string.Empty, Array.Empty<string>(), string.Empty, string.Empty,
                    GameMetadataParsing.SourceSteamGridDb, "https://www.steamgriddb.com/game/" + g.GameId, g.Url), null);
            }
            return (info with { CoverUrl = g.Url }, info.CoverUrl);
        }

        // ---- IGDB (optional, user-supplied Twitch application credentials). The secret only goes to id.twitch.tv; redirects are never followed.
        private static readonly HttpClient IgdbHttp = CreateIgdbClient();
        private static string? _igdbToken;
        private static DateTime _igdbTokenExpiresUtc;

        private static HttpClient CreateIgdbClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX/2.0 (local game library; https://github.com/Ruba94-cyber)");
            return client;
        }

        private sealed class IgdbSession
        {
            public IgdbSession(string clientId, string clientSecret) { ClientId = clientId; ClientSecret = clientSecret; }
            public string ClientId { get; }
            public string ClientSecret { get; }
            public bool Disabled { get; set; }
        }

        private static async Task<string?> GetIgdbTokenAsync(IgdbSession igdb, CancellationToken ct)
        {
            if (_igdbToken is not null && DateTime.UtcNow < _igdbTokenExpiresUtc) return _igdbToken;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = igdb.ClientId, ["client_secret"] = igdb.ClientSecret, ["grant_type"] = "client_credentials"
                })
            };
            using HttpResponseMessage response = await IgdbHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is 400 or 401 or 403)
            {
                igdb.Disabled = true;
                Interlocked.Exchange(ref _notice, "IGDB rejected the Client ID / Secret, so covers come from the other sources only. Check or replace them with \"IGDB key...\".");
                return null;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 100_000) { igdb.Disabled = status == 429; return null; }
            var parsed = GameMetadataParsing.ParseIgdbToken(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (parsed is not { } p) return null;
            _igdbToken = p.Token;
            _igdbTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, p.ExpiresInSeconds - 300));
            return _igdbToken;
        }

        private static async Task<GameInfoDto?> LookUpIgdbAsync(GameTitleDto title, string name, IgdbSession igdb, CancellationToken ct)
        {
            try
            {
                string? token = await GetIgdbTokenAsync(igdb, ct).ConfigureAwait(false);
                if (token is null) return null;
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
                {
                    Content = new StringContent(GameMetadataParsing.BuildIgdbQuery(name), Encoding.UTF8, "text/plain")
                };
                request.Headers.Add("Client-ID", igdb.ClientId);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage response = await IgdbHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status == 401) { _igdbToken = null; return null; }
                if (status is 403 or 429) { igdb.Disabled = true; return null; }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 2_000_000) return null;
                return GameMetadataParsing.ParseIgdbBest(title.TitleKey, name, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return null; // offline or an IGDB hiccup: the other sources still run
            }
        }

        private static async Task<(string Url, long GameId, string Name)?> LookUpSteamGridDbAsync(string name, SgdbSession sgdb, CancellationToken ct)
        {
            try
            {
                string? search = await GetSteamGridDbAsync(sgdb, "/search/autocomplete/" + Uri.EscapeDataString(name), ct).ConfigureAwait(false);
                if (search is null) return null;
                List<(long Id, string Name)> games = GameMetadataParsing.ParseSteamGridDbSearch(search);
                if (GameMetadataParsing.PickSteamGridDbGame(name, games) is not long id) return null;
                string? grids = await GetSteamGridDbAsync(sgdb,
                    "/grids/game/" + id + "?dimensions=600x900&types=static&mimes=image%2Fpng,image%2Fjpeg&nsfw=false&humor=false", ct).ConfigureAwait(false);
                if (grids is null || GameMetadataParsing.PickSteamGridDbGrid(grids) is not string url) return null;
                return (url, id, games.First(g => g.Id == id).Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return null; // SteamGridDB hiccup: the other sources still run
            }
        }

        /// <summary>One authenticated SteamGridDB GET. Null on any failure; a rejected key switches the source off for the run.</summary>
        private static async Task<string?> GetSteamGridDbAsync(SgdbSession sgdb, string path, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SgdbApi + path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sgdb.Key);
            using HttpResponseMessage response = await SgdbHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                sgdb.Disabled = true;
                Interlocked.Exchange(ref _notice, "SteamGridDB rejected the API key, so covers come from Steam and Wikipedia only. Check or replace the key with \"SteamGridDB key...\".");
                return null;
            }
            if (status == 429) { sgdb.Disabled = true; return null; }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 2_000_000) return null;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        private static async Task<GameInfoDto?> LookUpCatalogueAsync(GameTitleDto title, CancellationToken ct)
        {
            string name = CleanName(title.Name);
            bool pc = false;
            foreach (GameInstallationDto installation in title.Installations)
                if (string.Equals(installation.Platform, "PC", StringComparison.OrdinalIgnoreCase)) pc = true;

            if (pc)
            {
                // Steam's search returns nothing for some edition suffixes ("... DC"); the normalised words still match.
                foreach (string term in Variants(name, GameMetadataParsing.NormalizeTitle(name)))
                {
                    string search = await GetStringAsync(
                        "https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(term) + "&cc=us&l=en", ct).ConfigureAwait(false);
                    long? appId = GameMetadataParsing.PickSteamMatch(name, GameMetadataParsing.ParseSteamSearch(search));
                    if (appId is not long id) continue;
                    string details = await GetStringAsync(
                        "https://store.steampowered.com/api/appdetails?appids=" + id + "&cc=us&l=en", ct).ConfigureAwait(false);
                    GameInfoDto? steam = GameMetadataParsing.ParseSteamDetails(title.TitleKey, id, details);
                    if (steam is not null) return steam;
                }
            }

            // The extra words help for some titles and break the search for others, so both forms are tried.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int summaries = 0;
            foreach (string term in Variants(name, name + " video game"))
            {
                string pages = await GetStringAsync(
                    "https://en.wikipedia.org/w/rest.php/v1/search/title?limit=5&q=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false);
                foreach (string key in GameMetadataParsing.ParseWikipediaSearch(pages))
                {
                    if (!seen.Add(key)) continue;
                    if (++summaries > 4) return null;
                    string summary = await GetStringAsync(
                        "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(key), ct).ConfigureAwait(false);
                    GameInfoDto? wiki = GameMetadataParsing.ParseWikipediaSummary(title.TitleKey, name, summary);
                    if (wiki is not null) return wiki;
                }
            }
            return null;
        }

        private static IEnumerable<string> Variants(params string[] terms)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string term in terms)
                if (!string.IsNullOrWhiteSpace(term) && seen.Add(term.Trim())) yield return term.Trim();
        }

        private static string CleanName(string name)
        {
            int bracket = name.IndexOf('[');
            return (bracket > 0 ? name[..bracket] : name).Trim();
        }

        private static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return string.Empty;
            if (response.Content.Headers.ContentLength is > 2_000_000) return string.Empty;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        /// <summary>The image downloads carry no key (the CDNs are public). True when the cover was stored.</summary>
        private static async Task<bool> DownloadCoverAsync(string titleKey, string url, CancellationToken ct)
        {
            if (!GameMetadataParsing.IsAllowedImageUrl(url)) return false;
            using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            if (response.Content.Headers.ContentLength is long length && length > GameCoverStore.MaxBytes) return false;
            // A redirect must stay on an allowed host as well.
            if (response.RequestMessage?.RequestUri is Uri finalUri && !GameMetadataParsing.IsAllowedImageUrl(finalUri.AbsoluteUri)) return false;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            string? extension = GameCoverStore.DetectExtension(bytes);
            if (bytes.Length == 0 || bytes.Length > GameCoverStore.MaxBytes || extension is null) return false;
            string temp = Path.Combine(Path.GetTempPath(), "parux-cover-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                return GameCoverStore.Set(titleKey, temp) == GameCoverResult.Saved;
            }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }

        private static void Save(string titleKey, GameInfoDto? info)
        {
            Directory.CreateDirectory(InfoFolder);
            GameInfoDto value = info ?? new GameInfoDto(titleKey, string.Empty, string.Empty, Array.Empty<string>(), string.Empty, string.Empty, "none", string.Empty, null);
            string path = InfoPath(titleKey);
            string tmp = path + ".tmp";
            lock (FileGate)
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
        }
    }
}
