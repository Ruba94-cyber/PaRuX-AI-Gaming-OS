using System;
using System.Collections.Generic;
using System.IO;
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

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX/2.0 (local game library; https://github.com/Ruba94-cyber)");
            return client;
        }

        private static string InfoFolder => Path.Combine(AppPaths.PaRuXRoot, "game-info");

        private static string InfoPath(string titleKey) =>
            Path.Combine(InfoFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(titleKey))).ToLowerInvariant()[..32] + ".json");

        /// <summary>The stored description of a title, or null when none was found (or nothing was fetched yet).</summary>
        public static GameInfoDto? TryGetInfo(string titleKey)
        {
            try
            {
                string path = InfoPath(titleKey);
                if (!File.Exists(path)) return null;
                GameInfoDto? info = JsonSerializer.Deserialize<GameInfoDto>(File.ReadAllText(path), JsonOptions);
                return info is { Name.Length: > 0 } ? info : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }

        private static bool ShouldAsk(string titleKey)
        {
            try
            {
                string path = InfoPath(titleKey);
                if (!File.Exists(path)) return true;
                GameInfoDto? stored = JsonSerializer.Deserialize<GameInfoDto>(File.ReadAllText(path), JsonOptions);
                if (stored is { Name.Length: > 0 })
                    return stored.CoverUrl is not null && GameCoverStore.TryGetPath(titleKey) is null; // finish a missing cover
                return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > RetryUnknownAfter;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; }
        }

        /// <summary>Fetches missing covers/descriptions for at most 40 titles. Returns how many titles gained information.</summary>
        public static async Task<int> FetchMissingAsync(IReadOnlyList<GameTitleDto> titles, Func<CancellationToken, Task<bool>> gamingActive, CancellationToken ct)
        {
            int found = 0, asked = 0;
            foreach (GameTitleDto title in titles)
            {
                ct.ThrowIfCancellationRequested();
                if (await gamingActive(ct).ConfigureAwait(false)) break;
                if (title.Hidden || !ShouldAsk(title.TitleKey)) continue;
                if (++asked > MaxTitlesPerRun) break;
                try
                {
                    GameInfoDto? info = await LookUpAsync(title, ct).ConfigureAwait(false);
                    Save(title.TitleKey, info);
                    if (info is null) continue;
                    found++;
                    if (info.CoverUrl is not null && GameCoverStore.TryGetPath(title.TitleKey) is null)
                        await DownloadCoverAsync(title.TitleKey, info.CoverUrl, ct).ConfigureAwait(false);
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

        private static async Task<GameInfoDto?> LookUpAsync(GameTitleDto title, CancellationToken ct)
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
            foreach (string term in Variants(name, name + " video game"))
            {
                string pages = await GetStringAsync(
                    "https://en.wikipedia.org/w/rest.php/v1/search/title?limit=5&q=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false);
                foreach (string key in GameMetadataParsing.ParseWikipediaSearch(pages))
                {
                    if (!seen.Add(key) || seen.Count > 4) continue;
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

        private static async Task DownloadCoverAsync(string titleKey, string url, CancellationToken ct)
        {
            if (!GameMetadataParsing.IsAllowedImageUrl(url)) return;
            using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            if (response.Content.Headers.ContentLength is long length && length > GameCoverStore.MaxBytes) return;
            // A redirect must stay on an allowed host as well.
            if (response.RequestMessage?.RequestUri is Uri finalUri && !GameMetadataParsing.IsAllowedImageUrl(finalUri.AbsoluteUri)) return;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            string? extension = GameCoverStore.DetectExtension(bytes);
            if (bytes.Length == 0 || bytes.Length > GameCoverStore.MaxBytes || extension is null) return;
            string temp = Path.Combine(Path.GetTempPath(), "parux-cover-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                GameCoverStore.Set(titleKey, temp);
            }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }

        private static void Save(string titleKey, GameInfoDto? info)
        {
            Directory.CreateDirectory(InfoFolder);
            GameInfoDto value = info ?? new GameInfoDto(titleKey, string.Empty, string.Empty, Array.Empty<string>(), string.Empty, string.Empty, "none", string.Empty, null);
            string path = InfoPath(titleKey);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
