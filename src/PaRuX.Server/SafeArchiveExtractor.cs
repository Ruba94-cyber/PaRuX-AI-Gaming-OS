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

    public static async Task ExtractAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken ct,
        long maxExtractedBytes = DefaultMaxExtractedBytes)
    {
        if (maxExtractedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));
        string root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        long extracted = 0;
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
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                // Do not materialize Unix symlink entries as files or directories.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Symbolic-link entries are not permitted in managed ZIP extraction.");
                bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                await ExtractCurrentEntryAsync(entry.FullName, isDirectory,
                    _ => Task.FromResult<Stream>(entry.Open()), root, rootPrefix,
                    ct, maxExtractedBytes, () => extracted, value => extracted = value);
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
                    ct, maxExtractedBytes, () => extracted, value => extracted = value);
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
                    rootPrefix, ct, maxExtractedBytes, () => extracted, value => extracted = value);
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
        long maxExtractedBytes,
        Func<long> getExtracted,
        Action<long> setExtracted)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

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
        EnsureNoReparsePoints(outputPath);
        if (isDirectory)
        {
            Directory.CreateDirectory(outputPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        EnsureNoReparsePoints(outputPath);
        await using (Stream input = await openInput(ct))
        {
            await using var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                long extracted = getExtracted() + read;
                if (extracted > maxExtractedBytes)
                    throw new InvalidDataException(
                        "Archive expanded beyond the safety limit of " + maxExtractedBytes + " bytes.");
                setExtracted(extracted);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
    private static void EnsureNoReparsePoints(string path)
    {
        for (string? candidate = path; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            // Creating fresh paths is allowed; existing links are not.
            if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive destination includes a linked path.");
        }
    }

}
