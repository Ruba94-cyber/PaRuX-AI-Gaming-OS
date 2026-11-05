using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Everything the Gaming library list can be narrowed or ordered by. Empty values are not sent.</summary>
    public sealed record GameTitleListQuery
    {
        public string? Text { get; init; }
        public string? Source { get; init; }
        public string? Platform { get; init; }
        /// <summary>True: only installed titles. False: only not-installed titles. Null: both.</summary>
        public bool? Installed { get; init; }
        public bool FavoritesOnly { get; init; }
        public string? Tag { get; init; }
        public string? Collection { get; init; }
        /// <summary>"exclude" (default), "include" or "only".</summary>
        public string? Hidden { get; init; }
        /// <summary>One of <see cref="GameTitleSorts"/>; empty keeps the catalogue's name order.</summary>
        public string? Sort { get; init; }
        public bool Refresh { get; init; }
    }

    /// <summary>
    /// Typed client for the Gaming library endpoints added with the Playnite/Xbox style home (sorted and filtered
    /// title list, hidden state, atomic bulk edits, editing a saved manual entry). It uses the same authenticated
    /// control-plane path as <see cref="GamingOrchestratorClient"/>: the local Core endpoint and the X-PaRuX-Token header.
    /// </summary>
    public sealed class GamingLibraryClient
    {
        public static GamingLibraryClient Current { get; } = new GamingLibraryClient();

        private static readonly HttpClient HttpClient = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        private GamingLibraryClient() { }

        internal static string BuildTitlesPath(GameTitleListQuery query)
        {
            var parts = new List<string>();
            void Add(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) parts.Add(name + "=" + Uri.EscapeDataString(value.Trim()));
            }
            Add("q", query.Text);
            Add("source", query.Source);
            Add("platform", query.Platform);
            if (query.Installed is { } installed) parts.Add("installed=" + (installed ? "true" : "false"));
            if (query.FavoritesOnly) parts.Add("favorite=true");
            Add("tag", query.Tag);
            Add("collection", query.Collection);
            if (query.Hidden is "include" or "only") parts.Add("hidden=" + query.Hidden);
            Add("sort", query.Sort);
            if (query.Refresh) parts.Add("refresh=true");
            return "/api/v1/gaming/titles" + (parts.Count == 0 ? "" : "?" + string.Join("&", parts));
        }

        public Task<GamingCallResult<List<GameTitleDto>>> QueryTitlesAsync(GameTitleListQuery query, CancellationToken ct = default) =>
            SendAsync<List<GameTitleDto>>(HttpMethod.Get, BuildTitlesPath(query), null, ct);

        public Task<GamingCallResult<object>> SetTitleHiddenAsync(string key, bool hidden, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Put,
                "/api/v1/gaming/titles/" + Uri.EscapeDataString(key) + "/hidden?hidden=" + (hidden ? "true" : "false"), null, ct);

        /// <summary>One atomic edit of 1 to 100 titles. Tags/Collections replace the full list for every key.</summary>
        public Task<GamingCallResult<BulkGameTitleResult>> BulkUpdateTitlesAsync(BulkGameTitleRequest request, CancellationToken ct = default) =>
            SendAsync<BulkGameTitleResult>(HttpMethod.Put, "/api/v1/gaming/titles/bulk", request, ct);

        /// <summary>Off-by-default per-game launch hooks: the global switch and every configured title.</summary>
        public Task<GamingCallResult<GameHooksStateDto>> GetGameHooksAsync(CancellationToken ct = default) =>
            SendAsync<GameHooksStateDto>(HttpMethod.Get, "/api/v1/gaming/hooks", null, ct);

        public Task<GamingCallResult<object>> SetGameHooksEnabledAsync(bool enabled, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Put, "/api/v1/gaming/hooks/enabled?enabled=" + (enabled ? "true" : "false"), null, ct);

        /// <summary>Sets (or, with both phases null, clears) the hooks of one title. The Core validates every program.</summary>
        public Task<GamingCallResult<object>> SetTitleHooksAsync(string key, GameHooksDto hooks, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Put, "/api/v1/gaming/titles/" + Uri.EscapeDataString(key) + "/hooks", hooks, ct);

        /// <summary>
        /// Only previews a user-selected JSON export; the Core never reads file paths
        /// from this text, and this call does not insert or launch any games.
        /// The JSON is size-limited by Core before parsing.
        /// </summary>
        public Task<GamingCallResult<List<ExternalLibraryGamePreviewDto>>> PreviewExternalLibraryAsync(
            string provider, string json, CancellationToken ct = default) =>
            SendAsync<List<ExternalLibraryGamePreviewDto>>(HttpMethod.Post,
                "/api/v1/gaming/library/import-preview",
                new ExternalLibraryPreviewRequest(provider, json), ct);

        /// <summary>Read locally saved wishlist metadata. Never calls a game store.</summary>
        public Task<GamingCallResult<List<LocalWishlistItemDto>>> GetWishlistAsync(CancellationToken ct = default) =>
            SendAsync<List<LocalWishlistItemDto>>(HttpMethod.Get, "/api/v1/gaming/wishlist", null, ct);

        /// <summary>Save a public Steam app id and target with monitoring OFF.</summary>
        public Task<GamingCallResult<LocalWishlistItemDto>> AddWishlistItemAsync(
            LocalWishlistItemDto item, CancellationToken ct = default) =>
            SendAsync<LocalWishlistItemDto>(HttpMethod.Post, "/api/v1/gaming/wishlist", item, ct);

        /// <summary>Owner's separate explicit consent; still no automatic network polling.</summary>
        public Task<GamingCallResult<LocalWishlistItemDto>> SetWishlistWatchAsync(
            string productId, bool enabled, bool consent, CancellationToken ct = default) =>
            SendAsync<LocalWishlistItemDto>(HttpMethod.Put,
                "/api/v1/gaming/wishlist/" + Uri.EscapeDataString(productId) + "/watch",
                new LocalWishlistWatchUpdate(enabled, consent), ct);

        /// <summary>
        /// Only a direct user action may request one indicative Steam storefront quote.
        /// Discover must already be opted in; no polling or notification is started.
        /// </summary>
        public Task<GamingCallResult<LocalWishlistQuoteDto>> QuoteWishlistPriceAsync(
            string productId, bool consent, CancellationToken ct = default) =>
            SendAsync<LocalWishlistQuoteDto>(HttpMethod.Post,
                "/api/v1/gaming/wishlist/" + Uri.EscapeDataString(productId) + "/quote",
                new LocalWishlistQuoteRequest(consent), ct);

        public Task<GamingCallResult<object>> RemoveWishlistItemAsync(
            string productId, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Delete,
                "/api/v1/gaming/wishlist/" + Uri.EscapeDataString(productId), null, ct);

        /// <summary>Edits a saved manual or emulator entry. Never touches game files.</summary>
        public Task<GamingCallResult<ManualGameDto>> UpdateManualGameAsync(string id, UpdateManualGameRequest request, CancellationToken ct = default) =>
            SendAsync<ManualGameDto>(HttpMethod.Put, "/api/v1/gaming/library/" + Uri.EscapeDataString(id), request, ct);

        /// <summary>Owner-only offline temporary game mod workflows, no game memory injection or auto execution.</summary>
        public Task<GamingCallResult<List<ManualGameDto>>> GetManualGamesForModsAsync(CancellationToken ct = default) =>
            SendAsync<List<ManualGameDto>>(HttpMethod.Get, "/api/v1/gaming/library", null, ct);

        public Task<GamingCallResult<List<GameTemporaryModDto>>> GetTemporaryGameModsAsync(CancellationToken ct = default) =>
            SendAsync<List<GameTemporaryModDto>>(HttpMethod.Get, "/api/v1/gaming/mods/temporary", null, ct);

        public Task<GamingCallResult<List<GameModPreviewDto>>> GetPendingGameModPreviewsAsync(
            CancellationToken ct = default) =>
            SendAsync<List<GameModPreviewDto>>(HttpMethod.Get,
                "/api/v1/gaming/mods/temporary/previews", null, ct);

        public Task<GamingCallResult<GameModPreviewDto>> PreviewTemporaryGameModAsync(
            GameModProposalRequest request, CancellationToken ct = default) =>
            SendAsync<GameModPreviewDto>(HttpMethod.Post,
                "/api/v1/gaming/mods/temporary/preview", request, ct);

        public Task<GamingCallResult<GameTemporaryModDto>> ApplyTemporaryGameModAsync(
            string previewId, bool consent, CancellationToken ct = default) =>
            SendAsync<GameTemporaryModDto>(HttpMethod.Post,
                "/api/v1/gaming/mods/temporary/apply",
                new GameModApplyRequest(previewId, consent), ct);

        public Task<GamingCallResult<object>> RestoreTemporaryGameModAsync(
            string modId, bool consent, CancellationToken ct = default) =>
            SendAsync<object>(HttpMethod.Post,
                "/api/v1/gaming/mods/temporary/restore",
                new GameModRestoreRequest(modId, consent), ct);

        /// <summary>Open application windows that Quick Resume can pause, plus what is paused now.</summary>
        public Task<GamingCallResult<QuickResumeStatusDto>> GetQuickResumeAsync(CancellationToken ct = default) =>
            SendAsync<QuickResumeStatusDto>(HttpMethod.Get, "/api/v1/gaming/quick-resume", null, ct);

        public Task<GamingCallResult<QuickResumeActionResult>> QuickResumeActionAsync(string action, QuickResumeActionRequest request, CancellationToken ct = default) =>
            SendAsync<QuickResumeActionResult>(HttpMethod.Post, "/api/v1/gaming/quick-resume/" + action, request, ct);

        public Task<GamingCallResult<QuickResumeActionResult>> ResumeAllPausedAsync(CancellationToken ct = default) =>
            SendAsync<QuickResumeActionResult>(HttpMethod.Post, "/api/v1/gaming/quick-resume/resume-all", null, ct);

        private static async Task<GamingCallResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            try
            {
                ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
                request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
                using HttpResponseMessage response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
                return await GamingResponseReader.ReadAsync<T>(response, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return GamingCallResult<T>.Fail(ex.Message);
            }
        }
    }

    public enum GameCoverResult { Saved, NotFound, TooLarge, UnsupportedType, Failed }

    /// <summary>
    /// Optional local cover images. The user picks an image file; only a validated copy (png, jpg or webp, size capped,
    /// checked by file signature; the file is either chosen by the user or, only after the opt-in "Covers & info", downloaded from the Steam or Wikipedia CDN by GameMetadataService) is stored under the PaRuX data folder with a name
    /// derived from the content hash. A small index maps title keys to those names. The original file is never modified.
    /// </summary>
    public static class GameCoverStore
    {
        public const long MaxBytes = 5L * 1024 * 1024;
        private static readonly Regex StoredName = new("^[0-9a-f]{32}\\.(png|jpg|webp)$", RegexOptions.Compiled);
        private static readonly object Gate = new();

        private static string Folder => Path.Combine(AppPaths.PaRuXRoot, "game-covers");
        private static string IndexPath => Path.Combine(Folder, "covers.json");

        /// <summary>Signature check (not the file extension): ".png", ".jpg", ".webp" or null.</summary>
        public static string? DetectExtension(ReadOnlySpan<byte> head)
        {
            if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return ".png";
            if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
            if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return ".webp";
            return null;
        }

        private static Dictionary<string, string> LoadIndex()
        {
            try
            {
                if (!File.Exists(IndexPath)) return new Dictionary<string, string>(StringComparer.Ordinal);
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath));
                var clean = new Dictionary<string, string>(StringComparer.Ordinal);
                if (raw != null)
                    foreach (var pair in raw)
                        if (pair.Key.Length is > 0 and <= 300 && StoredName.IsMatch(pair.Value)) clean[pair.Key] = pair.Value;
                return clean;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private static void SaveIndex(Dictionary<string, string> index)
        {
            Directory.CreateDirectory(Folder);
            string tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(index));
            File.Move(tmp, IndexPath, overwrite: true);
        }

        /// <summary>Full path of the stored cover of a title, or null when it has none (or the copy is missing).</summary>
        public static string? TryGetPath(string titleKey)
        {
            if (string.IsNullOrEmpty(titleKey)) return null;
            lock (Gate)
            {
                if (!LoadIndex().TryGetValue(titleKey, out string? name)) return null;
                string path = Path.Combine(Folder, name);
                return File.Exists(path) ? path : null;
            }
        }

        public static GameCoverResult Set(string titleKey, string sourcePath)
        {
            if (string.IsNullOrEmpty(titleKey) || titleKey.Length > 300 || string.IsNullOrWhiteSpace(sourcePath))
                return GameCoverResult.NotFound;
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) return GameCoverResult.UnsupportedType;
            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists) return GameCoverResult.NotFound;
                if (info.Length > MaxBytes) return GameCoverResult.TooLarge;
                if (info.Length == 0) return GameCoverResult.UnsupportedType;
                byte[] bytes;
                using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bytes = new byte[(int)Math.Min(info.Length, MaxBytes + 1)];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read > MaxBytes) return GameCoverResult.TooLarge;
                    if (read < bytes.Length) Array.Resize(ref bytes, read);
                }
                string? detected = DetectExtension(bytes);
                if (detected is null) return GameCoverResult.UnsupportedType;
                string name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32] + detected;

                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    string target = Path.Combine(Folder, name);
                    if (!File.Exists(target))
                    {
                        string tmp = target + ".tmp";
                        File.WriteAllBytes(tmp, bytes);
                        File.Move(tmp, target, overwrite: true);
                    }
                    var index = LoadIndex();
                    index.TryGetValue(titleKey, out string? previous);
                    index[titleKey] = name;
                    SaveIndex(index);
                    DeleteIfUnreferenced(previous, index);
                }
                return GameCoverResult.Saved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return GameCoverResult.Failed;
            }
        }

        public static bool Remove(string titleKey)
        {
            try
            {
                lock (Gate)
                {
                    var index = LoadIndex();
                    if (!index.Remove(titleKey, out string? previous)) return true;
                    SaveIndex(index);
                    DeleteIfUnreferenced(previous, index);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void DeleteIfUnreferenced(string? name, Dictionary<string, string> index)
        {
            if (name is null || !StoredName.IsMatch(name) || index.ContainsValue(name)) return;
            try { File.Delete(Path.Combine(Folder, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leftover copy is harmless */ }
        }
    }
}
