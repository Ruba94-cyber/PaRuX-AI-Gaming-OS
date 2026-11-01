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

/// <summary>
/// A download the user asked for. There is no built-in catalogue: the address always comes from the user.
/// <paramref name="ExpectedSha256"/> is optional; when given, a mismatch deletes the file and fails the job.
/// </summary>
public sealed record GameDownloadRequest(
    string Url, string DestinationDirectory, string? FileName = null, string? ExpectedSha256 = null, int Priority = 0);

/// <param name="Source">Host and path only: query strings and fragments (which may carry tokens) are never stored or shown.</param>
/// <param name="Reason">Why a job is paused or failed, in plain words.</param>
/// <param name="VerifiedSha256">The SHA-256 of the finished file (computed locally); matches the expected value when one was given.</param>
public sealed record GameDownloadDto(
    string Id, string FileName, string Source, string State, string? Reason, long BytesDone, long? TotalBytes,
    int Priority, bool IntegrityChecked, string? VerifiedSha256, DateTime CreatedUtc, DateTime? FinishedUtc);

public sealed record GameDownloadLimitRequest(long BytesPerSecond);
