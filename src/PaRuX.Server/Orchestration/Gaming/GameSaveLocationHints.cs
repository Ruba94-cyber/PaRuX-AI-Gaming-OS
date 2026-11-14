using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Where save folders usually live on this PC. Injectable so tests never read the real profile.</summary>
internal sealed record SaveHintRoots(string? Documents, string? SavedGames, string? LocalAppData, string? RoamingAppData,
    string? LocalLow, IReadOnlyList<string> SteamRoots)
{
    internal static SaveHintRoots FromEnvironment(IEnumerable<string>? steamRoots = null)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new SaveHintRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            profile.Length == 0 ? null : Path.Combine(profile, "Saved Games"),
            local,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local.Length == 0 ? null : Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            (steamRoots ?? Array.Empty<string>()).ToList());
    }
}

/// <summary>
/// Read-only save-folder hints (GAM-005): the usual per-user save locations for a game name (Documents, Documents\My Games,
/// Saved Games, AppData, LocalLow), a small table of well-known titles, and Steam userdata for a Steam app id. Only folders that
/// exist, are on a local volume and are not links are returned, with a bounded file count. Nothing is read, copied or changed:
/// the person still chooses the folder, and a backup still goes through <see cref="GameSaveBackupService"/>.
/// </summary>
internal static class GameSaveLocationHints
{
    internal const int MaxHints = 8;
    internal const int MaxCountedFiles = 500;
    internal const int MaxNameLength = 120;

    // Well-known titles whose save folder name differs from the title. Relative to the named root; checked for existence only,
    // so a wrong or outdated entry can never produce a hint.
    private static readonly (string Name, string Root, string Relative)[] KnownTitles =
    {
        ("the witcher 3", "documents", @"The Witcher 3\gamesaves"),
        ("the witcher 3 wild hunt", "documents", @"The Witcher 3\gamesaves"),
        ("skyrim special edition", "documents", @"My Games\Skyrim Special Edition\Saves"),
        ("the elder scrolls v skyrim special edition", "documents", @"My Games\Skyrim Special Edition\Saves"),
        ("fallout 4", "documents", @"My Games\Fallout4\Saves"),
        ("terraria", "documents", @"My Games\Terraria"),
        ("elden ring", "roaming", "EldenRing"),
        ("dark souls iii", "roaming", "DarkSoulsIII"),
        ("dark souls remastered", "documents", @"NBGI\DARK SOULS REMASTERED"),
        ("stardew valley", "roaming", @"StardewValley\Saves"),
        ("minecraft", "roaming", @".minecraft\saves"),
        ("cyberpunk 2077", "savedgames", @"CD Projekt Red\Cyberpunk 2077"),
        ("hades", "savedgames", @"Hades"),
        ("baldur's gate 3", "local", @"Larian Studios\Baldur's Gate 3\PlayerProfiles"),
        ("hollow knight", "locallow", @"Team Cherry\Hollow Knight"),
    };

    internal static IReadOnlyList<GameSaveHintDto> Find(string? gameName, string? steamAppId, SaveHintRoots roots)
    {
        var found = new List<GameSaveHintDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string? path, string source)
        {
            if (found.Count >= MaxHints || string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!seen.Add(full.TrimEnd(Path.DirectorySeparatorChar))) return;
            if (!GameSaveBackupService.IsBackupableFolder(full)) return;
            (int files, DateTime? newest) = Inspect(full);
            if (files == 0) return;
            found.Add(new GameSaveHintDto(full, source, files, files >= MaxCountedFiles, newest));
        }

        string? name = CleanName(gameName);
        if (name is not null)
        {
            string key = Normalize(name);
            foreach ((string known, string root, string relative) in KnownTitles)
            {
                if (relative.Length == 0 || known != key) continue;
                Consider(Combine(RootOf(roots, root), relative), "known-title");
            }
            Consider(Combine(roots.Documents, @"My Games", name), "documents");
            Consider(Combine(roots.Documents, name), "documents");
            Consider(Combine(roots.SavedGames, name), "saved-games");
            Consider(Combine(roots.LocalAppData, name), "appdata-local");
            Consider(Combine(roots.RoamingAppData, name), "appdata-roaming");
            Consider(Combine(roots.LocalLow, name), "appdata-locallow");
        }

        if (IsSteamAppId(steamAppId))
        {
            foreach (string steam in roots.SteamRoots)
            {
                string userdata = Combine(steam, "userdata") ?? string.Empty;
                IEnumerable<string> users;
                try { users = Directory.Exists(userdata) ? Directory.EnumerateDirectories(userdata).Take(16).ToList() : Array.Empty<string>(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (string user in users)
                    Consider(Path.Combine(user, steamAppId!, "remote"), "steam-cloud");
            }
        }
        return found;
    }

    private static string? RootOf(SaveHintRoots roots, string key) => key switch
    {
        "documents" => roots.Documents,
        "savedgames" => roots.SavedGames,
        "local" => roots.LocalAppData,
        "roaming" => roots.RoamingAppData,
        "locallow" => roots.LocalLow,
        _ => null,
    };

    private static string? Combine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try { return Path.Combine(new[] { root }.Concat(parts).ToArray()); }
        catch (ArgumentException) { return null; }
    }

    internal static bool IsSteamAppId(string? value) =>
        value is { Length: > 0 and <= 12 } && value.All(c => c is >= '0' and <= '9');

    /// <summary>A game name that is safe to use as one folder name: no separators, wildcards, control or reserved characters.</summary>
    internal static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string name = new string(raw.Trim().Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)
            && c is not (':' or '™' or '®' or '©')).ToArray()).Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.Length > MaxNameLength || name is "." or "..") return null;
        return name;
    }

    private static string Normalize(string name) =>
        string.Join(' ', name.ToLowerInvariant().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries));

    private static (int Files, DateTime? NewestUtc) Inspect(string folder)
    {
        int files = 0;
        DateTime? newest = null;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((folder, 0));
        try
        {
            while (pending.Count > 0 && files < MaxCountedFiles)
            {
                (string current, int depth) = pending.Pop();
                foreach (string file in Directory.EnumerateFiles(current))
                {
                    files++;
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (newest is null || written > newest) newest = written;
                    if (files >= MaxCountedFiles) break;
                }
                if (depth >= 3) continue;
                foreach (string child in Directory.EnumerateDirectories(current))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push((child, depth + 1));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a partly unreadable folder still counts what was read */ }
        return (files, newest);
    }
}
