namespace PaRuX.Server;

/// <summary>Downloads immutable, commit-addressed GitHub source archives without requiring Git.
/// Extraction goes through <see cref="SafeArchiveExtractor"/> (traversal, reparse-point, symlink, entry-count,
/// byte and compression-ratio quotas) and the completed tree is promoted atomically (SEC-F13).</summary>
public static class ManagedSourceArchive
{
    /// <summary>A source archive larger than this is refused while downloading.</summary>
    internal const long MaxArchiveBytes = 512L * 1024 * 1024;
    internal const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaxEntries = 50_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task EnsureGithubCommitAsync(
        string owner, string repository, string commitSha, string destination, CancellationToken ct)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) return;
        PaRuX.Contracts.StoreEditionPolicy.RequireExecutableDownloads();
        if (!SafeSlug(owner) || !SafeSlug(repository) || commitSha.Length != 40 || commitSha.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Managed source archive identity is invalid.");

        string parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Managed source destination has no parent directory.");
        Directory.CreateDirectory(parent);
        string archive = destination + ".zip.download";
        try
        {
            var uri = new Uri($"https://github.com/{owner}/{repository}/archive/{commitSha}.zip");
            using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared > MaxArchiveBytes)
                throw new InvalidDataException("Managed source archive exceeds the size limit.");
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes)
                        throw new InvalidDataException("Managed source archive exceeds the size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            await ExtractArchiveAsync(archive, destination, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(archive); } catch { }
        }
    }

    /// <summary>Extracts a downloaded GitHub source archive (one top-level folder) into <paramref name="destination"/>.
    /// Any unsafe entry, an exceeded quota or a layout with more than one top-level entry fails closed and leaves no
    /// partial tree behind.</summary>
    internal static async Task ExtractArchiveAsync(string archive, string destination, CancellationToken ct, int maxEntries = MaxEntries)
    {
        string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            await SafeArchiveExtractor.ExtractAsync(archive, staging, ct,
                maxExtractedBytes: MaxExtractedBytes, maxEntries: maxEntries).ConfigureAwait(false);

            string[] top = Directory.GetFileSystemEntries(staging);
            if (top.Length != 1 || !Directory.Exists(top[0]))
                throw new InvalidDataException("Managed source archive must contain exactly one top-level folder.");

            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(top[0], destination);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static bool SafeSlug(string value) => !string.IsNullOrWhiteSpace(value)
        && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
