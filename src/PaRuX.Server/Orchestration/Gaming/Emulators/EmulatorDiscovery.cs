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

    /// <summary>
    /// Traverse each eligible directory at most once for all registered emulators.
    /// A shared global budget and breadth-first root ordering prevent a missing
    /// early emulator from starving every later emulator/root. Only local regular,
    /// unlinked executable files are ever reported; none are started.
    /// </summary>
    public static IReadOnlyList<DetectedEmulator> Detect(
        IEnumerable<string> searchRoots, CancellationToken ct = default, int maxEntries = MaxDetectEntries)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        if (maxEntries is < 1 or > MaxDetectEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));

        var byName = new Dictionary<string, List<EmulatorDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (EmulatorDefinition emulator in EmulatorRegistry.Builtin)
            foreach (string name in emulator.ExecutableNames)
            {
                if (!byName.TryGetValue(name, out var definitions))
                    byName[name] = definitions = new List<EmulatorDefinition>();
                definitions.Add(emulator);
            }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Directory, int Depth)>();

        // Queue all roots first. A huge Program Files subtree must not consume
        // the entire budget before another user-installed emulator location is visited.
        foreach (string root in searchRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (seen.Count >= maxEntries) break;
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                    continue;
                string full = Path.GetFullPath(root);
                if (ManualGameLibrary.IsSafeDirectory(full) && seen.Add(full))
                    pending.Enqueue((full, 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException) { /* Ignore an unusable root. */ }
        }

        while (pending.Count > 0 && found.Count < EmulatorRegistry.Builtin.Count)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Dequeue();
            try
            {
                // The caller's folder can change between enumeration and inspection.
                if (!ManualGameLibrary.IsSafeDirectory(directory)) continue;
                foreach (var pair in byName)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pair.Value.All(d => found.ContainsKey(d.Id))) continue;
                    string candidate = Path.Combine(directory, pair.Key);
                    if (!File.Exists(candidate) ||
                        !CustomExecutableInputProvider.IsSafeHelperPath(candidate)) continue;
                    foreach (EmulatorDefinition definition in pair.Value)
                        found.TryAdd(definition.Id, candidate);
                }

                if (depth >= MaxDetectDepth || seen.Count >= maxEntries) continue;
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (seen.Count >= maxEntries) break;
                    // No junctions or symlink traversals, including the chosen root's ancestors.
                    if (!ManualGameLibrary.IsSafeDirectory(child)) continue;
                    string full = Path.GetFullPath(child);
                    if (seen.Add(full)) pending.Enqueue((full, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                // A missing or protected directory must not prevent another root from being visited.
            }
        }

        return EmulatorRegistry.Builtin
            .Where(d => found.ContainsKey(d.Id))
            .Select(d => new DetectedEmulator(d.Id, found[d.Id]))
            .ToArray();
    }

    public static EmulatorScanResult Scan(EmulatorDefinition definition, string? folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 1024 || folder.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            return new(Array.Empty<EmulatedGameCandidate>(), false, "Choose an absolute local folder.");
        string root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) return new(Array.Empty<EmulatedGameCandidate>(), false, "The folder does not exist.");
        // A root can be under a junction even when its own directory has no
        // reparse attribute. Refuse every linked ancestor and remote mapped volume,
        // before any ROM directory traversal or metadata read.
        if (!ManualGameLibrary.IsSafeDirectory(root))
            return new(Array.Empty<EmulatedGameCandidate>(), false, "Linked folders or linked ancestors are not scanned.");
        try
        {
            DriveType driveType = new DriveInfo(Path.GetPathRoot(root)!).DriveType;
            if (driveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                return new(Array.Empty<EmulatedGameCandidate>(), false, "Choose a local volume for emulator discovery.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(Array.Empty<EmulatedGameCandidate>(), false, "The selected local volume is unavailable.");
        }

        var games = new List<EmulatedGameCandidate>();
        int entries = 0;
        bool truncated = false;

        void Walk(string directory, int depth)
        {
            if (truncated) return;
            try
            {
                // Stream entries as they are visited. GetFiles/GetDirectories would
                // allocate the whole directory before the safety budget could apply.
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++entries > MaxScanEntries || games.Count >= MaxGames)
                    { truncated = true; return; }
                    if (!EmulatorRegistry.IsGameFile(definition, file) || IsLink(file)) continue;
                    long size;
                    try { size = new FileInfo(file).Length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    string title = definition.GameFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                        ? TitleForGenericFileName(file)
                        : GameFolderInspector.CleanTitle(Path.GetFileNameWithoutExtension(file));
                    games.Add(new EmulatedGameCandidate(file, title, size));
                }
                if (depth >= MaxScanDepth) return;
                foreach (string sub in Directory.EnumerateDirectories(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++entries > MaxScanEntries) { truncated = true; return; }
                    if (!IsLink(sub)) Walk(sub, depth + 1);
                    if (truncated) return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A user-inaccessible subtree does not invalidate other verified results.
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
