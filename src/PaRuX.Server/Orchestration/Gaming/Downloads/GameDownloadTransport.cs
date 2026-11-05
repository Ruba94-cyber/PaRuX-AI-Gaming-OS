using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

/// <summary>
/// The user's own client reports the job as paused (not PaRuX). The job becomes Paused until the user resumes it here.
/// </summary>
public sealed class GameDownloadPausedException : GameDownloadException
{
    public GameDownloadPausedException(string message) : base(message) { }
}

/// <summary>One download job. Mutable fields are guarded by the manager's lock.</summary>
internal sealed class GameDownloadJob
{
    public required string Id { get; init; }
    public string Kind { get; init; } = GameDownloadKinds.Http;
    /// <summary>The full source (URL with query, magnet with trackers, or .torrent path). In memory only, never persisted or shown.</summary>
    public string Target { get; init; } = string.Empty;
    public Uri? Url { get; init; }
    /// <summary>Privacy-safe display and history form of the source (host/path style).</summary>
    public string Source { get; init; } = string.Empty;
    public required string FileName { get; init; }
    public required string Directory { get; init; }
    public string? ExpectedSha256 { get; init; }
    public int Priority { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string State = GameDownloadStates.Queued;
    public string? Reason;
    public long BytesDone;
    public long? TotalBytes;
    public bool IntegrityChecked;
    public string? VerifiedSha256;
    // Session-local: never persist an absolute user folder path in download history.
    public string? PublishedPath;
    public DateTime? FinishedUtc;
    public CancellationTokenSource? Run;
    public bool UserPause;
    /// <summary>Handle of the job inside an external client (torrent info-hash). Session-local.</summary>
    /// <summary>The transfer backend chosen when queued; changing external-client settings does not switch an active job.</summary>
    public IGameDownloadTransport? ChosenTransport { get; init; }
    public volatile string? ExternalId;
    /// <summary>Torrent info-hash (hex), known from the validated magnet or .torrent file.</summary>
    public string? InfoHash { get; init; }
    public string PartPath => Path.Combine(Directory, FileName + ".parux-part");
    public string FinalPath => Path.Combine(Directory, FileName);
}

/// <summary>What a transport may use from the manager. Created per attempt so limits changed at runtime apply.</summary>
internal sealed class GameDownloadTransferContext
{
    public required GameDownloadJob Job { get; init; }
    public required Func<bool> GamingActive { get; init; }
    public required Func<string, long> FreeBytes { get; init; }
    public required long MaxFileBytes { get; init; }
    public required Func<int, CancellationToken, Task> Throttle { get; init; }
    /// <summary>Current manager network limit, read at runtime; zero means unlimited.</summary>
    public Func<long>? NetworkBytesPerSecond { get; init; }
    public required Action<long, long?> Report { get; init; }
    public required Func<bool> UserCancelled { get; init; }
}

/// <summary>
/// Moves the bytes of one job to <c>Job.PartPath</c>. A transport never publishes: the manager always re-hashes the
/// finished file itself, compares it with the expected SHA-256 and performs the final rename.
/// Transient failures are signalled with HttpRequestException/IOException so the manager retries a bounded number of times.
/// </summary>
internal interface IGameDownloadTransport
{
    Task FetchAsync(GameDownloadTransferContext context, CancellationToken ct);

    /// <summary>User cancelled: release anything the transport left behind outside PaRuX's own part file.</summary>
    Task AbandonAsync(GameDownloadJob job, CancellationToken ct);
}
