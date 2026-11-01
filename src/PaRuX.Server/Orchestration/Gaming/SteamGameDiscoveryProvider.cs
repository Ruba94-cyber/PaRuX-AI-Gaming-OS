using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Read-only Steam local installation metadata. Does not open links, run games, invoke
/// Steam APIs, download files or accept commands from appmanifest contents.
/// </summary>
public sealed class SteamGameDiscoveryProvider
{
    private static readonly Regex VdfPair = new(
        "\"([^\"]+)\"\\s+\"([^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly string _steamRoot;
    private const int MaxRoots = 32;
    private const int MaxGames = 10000;
    private const long MaxManifestBytes = 256 * 1024;

    public SteamGameDiscoveryProvider()
        : this(SteamInstallLocator.FindLibraryRoot() ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")) { }

    public SteamGameDiscoveryProvider(string steamRoot) =>
        _steamRoot = Path.GetFullPath(steamRoot);

    public Task<IReadOnlyList<DiscoveredGameDto>> DiscoverAsync(CancellationToken ct) =>
        Task.Run(() => Discover(ct), ct);

    internal IReadOnlyList<DiscoveredGameDto> Discover(CancellationToken ct)
    {
        var steamApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSteamAppsDirectory(_steamRoot, steamApps);

        string librariesFile = Path.Combine(_steamRoot, "steamapps", "libraryfolders.vdf");
        if (IsSmallFile(librariesFile))
        {
            try
            {
                string text = File.ReadAllText(librariesFile);
                foreach (Match pair in VdfPair.Matches(text))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.Equals(pair.Groups[1].Value, "path", StringComparison.OrdinalIgnoreCase)) continue;
                    if (steamApps.Count >= MaxRoots) break;
                    string candidate = pair.Groups[2].Value.Replace(@"\\", @"\");
                    AddSteamAppsDirectory(candidate, steamApps);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException)
            {
                // A malformed/unavailable library does not authorize scanning arbitrary paths.
            }
        }

        var games = new Dictionary<string, DiscoveredGameDto>(StringComparer.Ordinal);
        foreach (string directory in steamApps)
        {
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(directory, "appmanifest_*.acf", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            try
            {
                foreach (string manifest in manifests)
                {
                    ct.ThrowIfCancellationRequested();
                    if (games.Count >= MaxGames) return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (!IsSmallFile(manifest)) continue;
                    string text = File.ReadAllText(manifest);
                    string? id = FindValue(text, "appid");
                    string? name = FindValue(text, "name");
                    string? installFolder = FindValue(text, "installdir");
                    if (id is null || name is null || installFolder is null ||
                        id.Length > 15 || !id.All(char.IsAsciiDigit) ||
                        name.Length is < 1 or > 200 ||
                        installFolder.Length is < 1 or > 150 ||
                        Path.IsPathRooted(installFolder) ||
                        installFolder is "." or ".." ||
                        installFolder.Contains("..", StringComparison.Ordinal) ||
                        installFolder.Contains('/') || installFolder.Contains('\\'))
                        continue;

                    string location = Path.Combine(directory, "common", installFolder);
                    if (!Directory.Exists(location)) continue;
                    games.TryAdd(id, new DiscoveredGameDto("steam", id, name, location,
                        InstallStateFromFlags(FindValue(text, "StateFlags"))));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RegexMatchTimeoutException)
            {
                // Keep other manifests usable. Never repair or overwrite Steam files.
            }
        }
        return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Valve's appmanifest StateFlags bit set: 1 uninstalled, 2 update required, 4 fully installed,
    /// 32 files missing, 128 files corrupt, 256 update running, 512 update paused, 1024 update started,
    /// 2048 uninstalling. "Fully installed" with "update required" (6) is still playable.
    /// </summary>
    internal static string InstallStateFromFlags(string? raw)
    {
        if (!uint.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint flags))
            return GameInstallStates.Unknown;
        const uint Uninstalled = 1, FullyInstalled = 4, FilesMissing = 32, FilesCorrupt = 128,
            UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024, Uninstalling = 2048;
        if ((flags & (Uninstalled | FilesMissing | FilesCorrupt | Uninstalling)) != 0) return GameInstallStates.Incomplete;
        if ((flags & FullyInstalled) != 0)
            return (flags & (UpdateRunning | UpdateStarted)) != 0 ? GameInstallStates.Updating : GameInstallStates.Installed;
        return (flags & (UpdateRunning | UpdatePaused | UpdateStarted)) != 0
            ? GameInstallStates.Updating : GameInstallStates.Incomplete;
    }

    private static string? FindValue(string text, string key)
    {
        foreach (Match pair in VdfPair.Matches(text))
            if (string.Equals(pair.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase))
                return pair.Groups[2].Value.Trim();
        return null;
    }

    private static bool IsSmallFile(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= MaxManifestBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void AddSteamAppsDirectory(string root, HashSet<string> directories)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
                root.StartsWith(@"\\", StringComparison.Ordinal))
                return;
            string directory = Path.Combine(Path.GetFullPath(root), "steamapps");
            if (Directory.Exists(directory) && directories.Count < MaxRoots) directories.Add(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
