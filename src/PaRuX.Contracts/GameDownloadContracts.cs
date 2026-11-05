namespace PaRuX.Contracts;

public static class GameDownloadStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string state) => state is Completed or Failed or Cancelled;
}

public static class GameDownloadKinds
{
    public const string Http = "http";
    public const string Magnet = "magnet";
    public const string TorrentFile = "torrentfile";

    /// <summary>Null or blank means the legacy default (http). Returns null for an unknown kind.</summary>
    public static string? Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return Http;
        string k = kind.Trim().ToLowerInvariant();
        return k is Http or Magnet or TorrentFile ? k : null;
    }
}

/// <summary>
/// A download the user asked for. There is no built-in catalogue: the address always comes from the user.
/// <paramref name="ExpectedSha256"/> is optional; when given, a mismatch deletes the file and fails the job.
/// <paramref name="Kind"/> is http (default, <paramref name="Url"/> is an https address), magnet (a magnet:?xt=urn:btih: link) or
/// torrentfile (<paramref name="Url"/> is a local .torrent path chosen by the user). The two torrent kinds need the optional
/// integrated single-file PaRuX transport by default, or the optional external client explicitly configured by the owner.
/// </summary>
public sealed record GameDownloadRequest(
    string Url, string DestinationDirectory, string? FileName = null, string? ExpectedSha256 = null, int Priority = 0,
    string Kind = GameDownloadKinds.Http);

/// <summary>
/// Product capability: embedded in PaRuX; nothing is listening, polling or uploading
/// until the owner explicitly enqueues a user-supplied source.
/// </summary>
public sealed record GameTorrentEngineStatusDto(
    bool EmbeddedAvailable, bool ExternalConfigured, string NewJobTransport,
    bool SingleFileOnly, bool NoAutomaticSeeding, bool NoCatalogue);

/// <summary>Loopback WebUI settings of the user's own qBittorrent. Held in memory only, never persisted.</summary>
public sealed record GameTorrentClientConfigRequest(int Port, string Username, string Password);

public sealed record GameTorrentClientStatusDto(bool Enabled, int? Port);

/// <param name="Source">Host and path only: query strings and fragments (which may carry tokens) are never stored or shown.</param>
/// <param name="Reason">Why a job is paused or failed, in plain words.</param>
/// <param name="VerifiedSha256">The SHA-256 of the finished file (computed locally); matches the expected value when one was given.</param>
public sealed record GameDownloadDto(
    string Id, string FileName, string Source, string State, string? Reason, long BytesDone, long? TotalBytes,
    int Priority, bool IntegrityChecked, string? VerifiedSha256, DateTime CreatedUtc, DateTime? FinishedUtc);

public sealed record GameDownloadLimitRequest(long BytesPerSecond);
