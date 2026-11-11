using System.Text.RegularExpressions;

namespace PaRuX.Contracts;

/// <summary>Raised by a Core service that was asked to do something the Store edition does not allow.</summary>
public sealed class StoreEditionRestrictedException : InvalidOperationException
{
    public StoreEditionRestrictedException(string message) : base(message) { }
}

/// <summary>
/// The single Store-edition policy (Microsoft Store / MSIX): no embedded torrent engine and no
/// download or installation of executable code. The Core is authoritative: one HTTP guard refuses
/// the matching routes before any network or disk activity, and the few services that do the work
/// call <see cref="RequireExecutableDownloads"/> / <see cref="RequireTorrent"/> as defense in depth.
/// The UI reads <see cref="AllowsExecutableDownloads"/> / <see cref="AllowsTorrent"/> only to hide or
/// disable controls. Model files, other pure data and "locate existing" are never affected.
/// </summary>
public static class StoreEditionPolicy
{
    public const string Reason = "Not available in the Microsoft Store edition.";
    public const string ExecutableDownloadsReason =
        "Not available in the Microsoft Store edition: PaRuX does not download or install programs there. Use the bundled engine, or locate software you installed yourself.";
    public const string TorrentReason = "Not available in the Microsoft Store edition: the torrent engine is not included.";

    private static readonly AsyncLocal<bool?> TestOverride = new();

    /// <summary>True in the Store edition (package identity or the explicit environment signal).</summary>
    public static bool IsActive => TestOverride.Value ?? PackageIdentity.IsStoreEdition;

    public static bool AllowsExecutableDownloads => PackageIdentity.AllowsExecutableDownloadsFor(IsActive);
    public static bool AllowsTorrent => PackageIdentity.AllowsTorrentFor(IsActive);

    /// <summary>Test seam: simulates (or denies) the Store edition for the current async flow only.</summary>
    internal static IDisposable Simulate(bool storeEdition)
    {
        bool? previous = TestOverride.Value;
        TestOverride.Value = storeEdition;
        return new Restore(previous);
    }

    private sealed class Restore(bool? previous) : IDisposable
    {
        public void Dispose() => TestOverride.Value = previous;
    }

    public static void RequireExecutableDownloads()
    {
        if (!AllowsExecutableDownloads) throw new StoreEditionRestrictedException(ExecutableDownloadsReason);
    }

    public static void RequireTorrent()
    {
        if (!AllowsTorrent) throw new StoreEditionRestrictedException(TorrentReason);
    }

    private sealed record Rule(Regex Path, bool OnlyMutating, bool Torrent);

    private static Rule Exec(string pattern, bool onlyMutating = true) => new(Compile(pattern), onlyMutating, false);

    private static Regex Compile(string pattern)
        => new("^" + pattern + "/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Every Core route that downloads or installs executable code (runtimes, engines, Python tooling,
    // ComfyUI, game archives/installers) or drives the torrent engine. Model/data downloads
    // (/api/v1/models/*, datasets, LoRA, media companions, model updates) are intentionally absent.
    private static readonly Rule[] Rules =
    [
        Exec("/api/v1/runtime/builds/available", onlyMutating: false),
        Exec("/api/v1/runtime/builds/install(/.*)?"),
        Exec("/api/v1/runtime/engines/exllamav3/install(/.*)?"),
        Exec("/api/v1/maintenance/runtime-update/run-now"),
        Exec("/api/v1/embeddings/engine/install"),
        Exec("/api/v1/reranker/engine/install"),
        Exec("/api/v1/avatar/engine/install"),
        Exec("/api/v1/training/engine/install"),
        Exec("/api/v2/training/engine/install"),
        Exec("/api/v1/adapters/conversion/install"),
        Exec("/api/v1/speech/engine/install"),
        Exec("/api/v1/threed/engine/install"),
        Exec("/api/v1/cutout/engine/install"),
        Exec("/api/v2/media/engines/ref2va/install"),
        Exec("/api/v2/media/engines/diffusers/install"),
        Exec("/api/v1/media/setup/start"),
        // Game downloads (HTTP(S) archives, magnets, .torrent files): installer/executable content.
        Exec("/api/v1/gaming/downloads"),
        Exec("/api/v1/gaming/downloads/[^/]+/(resume|portable/retry)"),
        new(Compile("/api/v1/gaming/downloads/torrent-client"), true, true),
    ];

    /// <summary>
    /// Pure rule used by the Core guard: the readable refusal for this request in the Store edition, or
    /// null when it is allowed. Read-only requests are allowed except the one that queries a release feed.
    /// </summary>
    public static string? BlockedReason(bool storeEdition, string? method, string? path)
    {
        if (!storeEdition || string.IsNullOrEmpty(path)) return null;
        bool mutating = !(string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase));
        foreach (Rule rule in Rules)
        {
            if (rule.OnlyMutating && !mutating) continue;
            if (!rule.Path.IsMatch(path)) continue;
            return rule.Torrent ? TorrentReason : ExecutableDownloadsReason;
        }
        return null;
    }
}
