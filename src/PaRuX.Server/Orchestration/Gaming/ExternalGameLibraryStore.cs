using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Owner-managed, local, metadata-only imports from a previewed third-party
/// export. Never retains launch paths, arguments, credentials, account state or
/// install rights, and never executes a game or opens a vendor application.
/// </summary>
public sealed class ExternalGameLibraryStore
{
    private const int MaximumEntries = 1000;
    private const int MaximumBytes = 512 * 1024;
    private sealed record StoreFile(int Version, List<ExternalLibraryGamePreviewDto> Entries);
    private readonly string _path;
    private readonly object _gate = new();

    public ExternalGameLibraryStore()
        : this(Path.Combine(PaRuXDataRoot.Path, "server", "external-game-titles.json")) { }

    public ExternalGameLibraryStore(string path) => _path = Path.GetFullPath(path);

    public IReadOnlyList<ExternalLibraryGamePreviewDto> List()
    {
        lock (_gate) return Read().ToArray();
    }

    public ExternalLibraryImportResultDto Import(IReadOnlyList<ExternalLibraryGame> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is < 1 or > MaximumEntries ||
            candidates.Any(item => item is null || !Valid(item.Provider, item.ExternalId, item.Title)))
            throw new ArgumentException("Select at least one valid previewed title.");
        lock (_gate)
        {
            using FileStream lease = AcquireWriteLease();
            List<ExternalLibraryGamePreviewDto> items = Read();
            var present = new HashSet<string>(
                items.Select(x => Identity(x.Provider, x.ExternalId)), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            int alreadyPresent = 0;
            foreach (ExternalLibraryGame candidate in candidates)
            {
                if (!present.Add(Identity(candidate.Provider, candidate.ExternalId)))
                {
                    alreadyPresent++;
                    continue; // no implicit renames or clobbering of older owner imports
                }
                items.Add(new ExternalLibraryGamePreviewDto(candidate.Provider,
                    candidate.ExternalId, candidate.Title));
                added++;
                if (items.Count > MaximumEntries)
                    throw new InvalidOperationException("External library limit reached.");
            }
            if (added > 0) Write(items);
            return new ExternalLibraryImportResultDto(added, alreadyPresent, items.Count);
        }
    }

    public bool Remove(string provider, string externalId)
    {
        if (!Valid(provider, externalId, "x"))
            throw new ArgumentException("Invalid imported game identity.");
        lock (_gate)
        {
            using FileStream lease = AcquireWriteLease();
            List<ExternalLibraryGamePreviewDto> items = Read();
            int removed = items.RemoveAll(x =>
                string.Equals(x.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Write(items);
            return true;
        }
    }

    private static string Identity(string provider, string id) => provider + "\u0000" + id;

    private static bool Valid(string? provider, string? id, string? title) =>
        provider is "playnite" or "heroic" or "lutris" &&
        id is { Length: > 0 and <= 200 } && !id.Any(char.IsControl) &&
        title is { Length: > 0 and <= 200 } && !title.Any(char.IsControl);

    private void EnsureSafeStorage()
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (!ManualGameLibrary.IsSafeDirectory(directory))
            throw new InvalidDataException("External library metadata directory is unsafe.");
        foreach (string file in new[] { _path, _path + ".lock" })
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null ||
                (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("External library metadata cannot use links.");
        }
    }

    private FileStream AcquireWriteLease()
    {
        EnsureSafeStorage();
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5))
            { Thread.Sleep(30); }
        }
    }

    private List<ExternalLibraryGamePreviewDto> Read()
    {
        EnsureSafeStorage();
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > MaximumBytes)
            throw new InvalidDataException("Saved external library exceeds the allowed size.");
        StoreFile? data;
        try { data = JsonSerializer.Deserialize<StoreFile>(File.ReadAllBytes(_path)); }
        catch (JsonException ex)
        { throw new InvalidDataException("Invalid saved external game library.", ex); }
        if (data is null || data.Version != 1 || data.Entries is null ||
            data.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Unsupported saved external game library.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Entries)
            if (item is null || !Valid(item.Provider, item.ExternalId, item.Title) ||
                !seen.Add(Identity(item.Provider, item.ExternalId)))
                throw new InvalidDataException("Invalid or duplicated saved external game.");
        return data.Entries;
    }

    private void Write(List<ExternalLibraryGamePreviewDto> items)
    {
        EnsureSafeStorage();
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new StoreFile(1, items));
        if (content.Length > MaximumBytes)
            throw new InvalidOperationException("External library metadata exceeds the limit.");
        string temp = Path.Combine(Path.GetDirectoryName(_path)!,
            ".external-game-titles-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(content);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
