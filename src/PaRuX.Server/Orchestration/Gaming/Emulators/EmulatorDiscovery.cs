using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming.Emulators;

public sealed record DetectedEmulator(string EmulatorId, string ExecutablePath);

public sealed record EmulatedGameCandidate(string Path, string Title, long SizeBytes);

public sealed record EmulatorScanResult(IReadOnlyList<EmulatedGameCandidate> Games, bool Truncated, string? Error);

/// <summary>
/// Finds emulators the user already installed and game files in folders the user chose. Read-only: it lists directory
/// entries and checks that an executable is a regular, local, non-linked file; it never runs or opens anything.
/// </summary>
public static class EmulatorDiscovery
{
    public const int MaxScanEntries = 20_000;
    public const int MaxGames = 2_000;
    public const int MaxScanDepth = 6;
    private const int MaxDetectDepth = 2;
    public const int MaxDetectEntries = 4_000;

    public static IReadOnlyList<string> DefaultSearchRoots()
    {
        var roots = new List<string>();
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData,
        })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) roots.Add(path);
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "Programs"));
        return roots;
    }

    public static IReadOnlyList<DetectedEmulator> Detect(
        IEnumerable<string> searchRoots, CancellationToken ct = default, int maxEntries = MaxDetectEntries)
    {
        if (maxEntries is < 1 or > MaxDetectEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        int remaining = maxEntries;
        var found = new List<DetectedEmulator>();
        foreach (EmulatorDefinition definition in EmulatorRegistry.Builtin)
        {
            string? match = null;
            foreach (string root in searchRoots)
            {
                ct.ThrowIfCancellationRequested();
                if (remaining <= 0) return found;
                match = FindExecutable(root, definition.ExecutableNames, MaxDetectDepth, ref remaining, ct);
                if (match is not null) break;
            }
            if (match is not null) found.Add(new DetectedEmulator(definition.Id, match));
        }
        return found;
    }

    private static string? FindExecutable(
        string directory, IReadOnlyList<string> names, int depth, ref int remaining, CancellationToken ct)
    {
        if (remaining-- <= 0 || !Directory.Exists(directory)) return null;
        try
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) && CustomExecutableInputProvider.IsSafeHelperPath(candidate)) return candidate;
            }
            if (depth <= 0) return null;
            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                if (remaining <= 0) return null;
                string? hit = FindExecutable(sub, names, depth - 1, ref remaining, ct);
                if (hit is not null) return hit;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static EmulatorScanResult Scan(EmulatorDefinition definition, string? folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 1024 || folder.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            return new(Array.Empty<EmulatedGameCandidate>(), false, "Choose an absolute local folder.");
        string root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) return new(Array.Empty<EmulatedGameCandidate>(), false, "The folder does not exist.");
        if (IsLink(root)) return new(Array.Empty<EmulatedGameCandidate>(), false, "Linked folders are not scanned.");

        var games = new List<EmulatedGameCandidate>();
        int entries = 0;
        bool truncated = false;

        void Walk(string directory, int depth)
        {
            if (truncated) return;
            string[] files, subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = depth < MaxScanDepth ? Directory.GetDirectories(directory) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaxScanEntries || games.Count >= MaxGames) { truncated = true; return; }
                if (!EmulatorRegistry.IsGameFile(definition, file) || IsLink(file)) continue;
                long size;
                try { size = new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                // EBOOT.BIN is named the same in every game: the title comes from the folder that holds it.
                string title = definition.GameFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                    ? TitleForGenericFileName(file)
                    : GameFolderInspector.CleanTitle(Path.GetFileNameWithoutExtension(file));
                games.Add(new EmulatedGameCandidate(file, title, size));
            }
            foreach (string sub in subdirectories)
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaxScanEntries) { truncated = true; return; }
                if (!IsLink(sub)) Walk(sub, depth + 1);
            }
        }

        Walk(root, 0);
        return new(games.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Path, StringComparer.OrdinalIgnoreCase).ToArray(), truncated, null);
    }

    private static readonly HashSet<string> StructuralFolders = new(StringComparer.OrdinalIgnoreCase) { "USRDIR", "PS3_GAME" };

    /// <summary>
    /// EBOOT.BIN has the same name in every game, so the title comes from the folder that holds the game:
    /// .../GameName/PS3_GAME/USRDIR/EBOOT.BIN -> GameName (structural folders are skipped).
    /// </summary>
    public static string TitleForGenericFileName(string file)
    {
        DirectoryInfo? directory = new FileInfo(file).Directory;
        while (directory is not null && StructuralFolders.Contains(directory.Name)) directory = directory.Parent;
        return GameFolderInspector.CleanTitle(directory?.Name ?? Path.GetFileNameWithoutExtension(file));
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
