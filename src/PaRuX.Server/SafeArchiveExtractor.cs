using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace PaRuX.Server;

/// <summary>
/// Extracts archives with paths/quotas owned by PaRuX. ZIP uses only the .NET BCL;
/// SharpCompress is limited to formats such as 7z that the BCL cannot decode.
/// Neither format decoder chooses file paths or bypasses PaRuX's safety checks.
/// </summary>
internal static class SafeArchiveExtractor
{
    private const long DefaultMaxExtractedBytes = 64L * 1024 * 1024 * 1024;
    /// <summary>Upper bound on entries in one archive (SEC-F13); real runtime archives hold a few thousand.</summary>
    internal const int DefaultMaxEntries = 100_000;
    /// <summary>Expanded bytes may not exceed this multiple of the archive's own size (zip-bomb guard, SEC-F13).</summary>
    internal const int DefaultMaxCompressionRatio = 200;
    /// <summary>Output below this size is never judged by the ratio, so tiny archives of repetitive text stay valid.</summary>
    internal const long RatioGraceBytes = 32L * 1024 * 1024;

    /// <summary>Running totals for one extraction, shared by every entry.</summary>
    private sealed class Quota
    {
        public long MaxBytes;
        public int MaxEntries;
        public long RatioLimitBytes;
        public long Extracted;
        public int Entries;
        public bool EnforceRatio = true;
        /// <summary>Only links and case/duplicate collisions are skipped (and listed here); traversal always throws.</summary>
        public List<string>? Skipped;
        /// <summary>When set, reparse points are only rejected at or below this PaRuX-owned folder.</summary>
        public string? OwnedRoot;

        public void CountEntry()
        {
            if (++Entries > MaxEntries)
                throw new InvalidDataException("Archive has more than " + MaxEntries + " entries.");
        }

        public void Charge(int bytes)
        {
            if (bytes > MaxBytes - Extracted)
                throw new InvalidDataException("Archive expanded beyond the safety limit of " + MaxBytes + " bytes.");
            Extracted += bytes;
            if (EnforceRatio && Extracted > RatioGraceBytes && Extracted > RatioLimitBytes)
                throw new InvalidDataException("Archive expands far beyond its compressed size (possible decompression bomb). "
                    + "If you trust this archive, extract it yourself and add the folder manually.");
        }
    }

    public static async Task ExtractAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken ct,
        long maxExtractedBytes = DefaultMaxExtractedBytes,
        int maxEntries = DefaultMaxEntries,
        int maxCompressionRatio = DefaultMaxCompressionRatio,
        bool enforceCompressionRatio = true,
        string? ownedRoot = null,
        List<string>? skippedEntries = null)
    {
        if (maxExtractedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxCompressionRatio <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompressionRatio));
        long archiveBytes = new FileInfo(archivePath).Length;
        var quota = new Quota
        {
            MaxBytes = maxExtractedBytes,
            MaxEntries = maxEntries,
            RatioLimitBytes = archiveBytes > long.MaxValue / maxCompressionRatio ? long.MaxValue : archiveBytes * maxCompressionRatio,
            EnforceRatio = enforceCompressionRatio,
            Skipped = skippedEntries,
            OwnedRoot = ownedRoot is null ? null : Path.GetFullPath(ownedRoot)
        };
        string root = Path.GetFullPath(destinationRoot);
        // Validate the entire existing parent chain BEFORE creating any output.
        // A junction in the destination parent must never redirect extraction.
        EnsureNoReparsePoints(root, quota.OwnedRoot);
        Directory.CreateDirectory(root);
        EnsureNoReparsePoints(root, quota.OwnedRoot);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        byte[] signature = new byte[6];
        int signatureLength;
        using (FileStream signatureStream = File.OpenRead(archivePath))
            signatureLength = signatureStream.Read(signature, 0, signature.Length);

        bool isSevenZip = signatureLength == 6 &&
            signature.AsSpan().SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
        bool isZip = signatureLength >= 4 &&
            signature[0] == 0x50 && signature[1] == 0x4B &&
            (signature[2] == 0x03 && signature[3] == 0x04 ||
             signature[2] == 0x05 && signature[3] == 0x06 ||
             signature[2] == 0x07 && signature[3] == 0x08);

        // Sniff the content, not the extension: a genuine ZIP staged as .7z
        // must still use the dependency-free ZIP reader.
        if (isZip)
        {
            await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > maxEntries)
                throw new InvalidDataException("Archive has more than " + maxEntries + " entries.");
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                // Do not materialize Unix symlink entries as files or directories.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    if (quota.Skipped is null)
                        throw new InvalidDataException("Symbolic-link entries are not permitted in managed ZIP extraction.");
                    quota.Skipped.Add(entry.FullName + " (symbolic link)");
                    continue;
                }
                bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                await ExtractCurrentEntryAsync(entry.FullName, isDirectory,
                    _ => Task.FromResult<Stream>(entry.Open()), root, rootPrefix,
                    ct, quota);
            }
        }
        else if (isSevenZip)
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            using IReader reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                await ExtractCurrentEntryAsync(reader.Entry.Key ?? string.Empty, reader.Entry.IsDirectory,
                    _ => Task.FromResult<Stream>(reader.OpenEntryStream()), root, rootPrefix,
                    ct, quota);
            }
        }
        else
        {
            await using var reader = await ReaderFactory.OpenAsyncReader(archivePath, new ReaderOptions(), ct);
            while (await reader.MoveToNextEntryAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                await ExtractCurrentEntryAsync(reader.Entry.Key ?? string.Empty, reader.Entry.IsDirectory,
                    async token => await reader.OpenEntryStreamAsync(token), root,
                    rootPrefix, ct, quota);
            }
        }
    }

    private static async Task ExtractCurrentEntryAsync(
        string key,
        bool isDirectory,
        Func<CancellationToken, Task<Stream>> openInput,
        string root,
        string rootPrefix,
        CancellationToken ct,
        Quota quota)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        quota.CountEntry();
        if (!IsSafePortableEntryName(key))
            throw new InvalidDataException("Archive entry uses a reserved or ambiguous Windows path.");

        string normalized = key.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException("Archive contains an unsafe path: " + key);

        string outputPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!outputPath.StartsWith(rootPrefix, PaRuXPathSafety.PathComparison))
            throw new InvalidDataException("Archive entry escapes the extraction directory: " + key);

        // Reject an existing symlink/junction in any ancestor, including the
        // destination itself. This prevents an archive from following a linked
        // output directory that points outside the extraction root.
        EnsureNoReparsePoints(outputPath, quota.OwnedRoot);
        if (isDirectory)
        {
            Directory.CreateDirectory(outputPath);
            return;
        }

        // Extract to a random sibling temp file and expose the final name only
        // after the entire entry passed quota and cancellation checks. Failed
        // writes or duplicate archive entries must not leave a runnable partial
        // runtime file under the expected name.
        string parent = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoints(outputPath, quota.OwnedRoot);
        string stagingPath = Path.Combine(parent,
            ".parux-extract-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (Stream input = await openInput(ct))
            {
                await using var output = new FileStream(
                    stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    quota.Charge(read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(outputPath, quota.OwnedRoot);
            // Same-directory move does not overwrite an existing destination.
            // A duplicate entry fails closed and preserves the first file; in lenient mode (source archives) a
            // duplicate or case-colliding entry is skipped and reported instead.
            try { File.Move(stagingPath, outputPath); }
            catch (IOException) when (quota.Skipped is not null && File.Exists(outputPath))
            {
                quota.Skipped.Add(key + " (duplicate or case-colliding name)");
            }
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

    }
    /// <summary>
    /// Reject names whose Windows normalization or DOS device interpretation
    /// can differ from the entry's literal spelling. Treat archive metadata as
    /// untrusted on every platform; keep one portable extraction policy.
    /// </summary>
    internal static bool IsSafePortableEntryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 4096 ||
            name.Any(char.IsControl)) return false;

        string[] segments = name.Split(new[] { '/', '\\' }, StringSplitOptions.None);
        foreach (string segment in segments)
        {
            // A final slash denotes a directory entry, but a doubled slash
            // or an interior empty segment is ambiguous on Windows.
            if (segment.Length == 0)
            {
                continue;
            }
            if (segment == ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
                return false;
            string basename = segment.Split('.')[0];
            if (IsReservedDosDevice(basename)) return false;
        }
        for (int index = 0; index < segments.Length; index++)
            if (segments[index].Length == 0 && index != segments.Length - 1)
                return false;
        return true;
    }

    private static bool IsReservedDosDevice(string basename)
    {
        string upper = basename.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL") return true;
        return upper.Length == 4 &&
               (upper.StartsWith("COM", StringComparison.Ordinal) ||
                upper.StartsWith("LPT", StringComparison.Ordinal)) &&
               upper[3] is >= '1' and <= '9';
    }

    private static bool IsAtOrBelow(string candidate, string root)
    {
        string full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string owned = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(owned, PaRuXPathSafety.PathComparison)
            || full.StartsWith(owned + Path.DirectorySeparatorChar, PaRuXPathSafety.PathComparison);
    }

    private static void EnsureNoReparsePoints(string path, string? ownedRoot = null)
    {
        for (string? candidate = path; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            // Ancestors above the PaRuX-owned root were chosen by the user or the OS (a data root under a junction or a
            // OneDrive folder is legitimate); only the owned tree is checked.
            if (ownedRoot != null && !IsAtOrBelow(candidate, ownedRoot)) break;
            // Creating fresh paths is allowed; existing links are not.
            if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive destination includes a linked path.");
        }
    }

}
