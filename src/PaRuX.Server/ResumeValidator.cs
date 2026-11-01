using System.Net.Http.Headers;

namespace PaRuX.Server;

/// <summary>
/// Resume safety for partial downloads: the strong ETag seen on the first request is stored beside the
/// .partial file and replayed as If-Range, and a 206 is only accepted when its Content-Range starts exactly at
/// the number of bytes already on disk. Best effort by design: a missing or unreadable validator never blocks a
/// download (the pinned SHA-256 remains the final integrity check).
/// </summary>
internal static class ResumeValidator
{
    private const long MaxFileBytes = 1024;

    private static string PathFor(string partialPath) => partialPath + ".validator";

    public static void Write(string partialPath, EntityTagHeaderValue? etag)
    {
        try
        {
            if (etag is null || etag.IsWeak) { Delete(partialPath); return; }
            File.WriteAllText(PathFor(partialPath), etag.Tag);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static EntityTagHeaderValue? Read(string partialPath)
    {
        try
        {
            string path = PathFor(partialPath);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
            string tag = File.ReadAllText(path).Trim();
            return tag.Length > 2 && !tag.StartsWith("W/", StringComparison.Ordinal) &&
                   EntityTagHeaderValue.TryParse(tag, out EntityTagHeaderValue? parsed) ? parsed : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Delete(string partialPath)
    {
        try { File.Delete(PathFor(partialPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>True only when the server resumed exactly at <paramref name="alreadyDownloaded"/>.</summary>
    public static bool ContentRangeMatches(ContentRangeHeaderValue? range, long alreadyDownloaded) =>
        range is not null && range.From == alreadyDownloaded;
}
