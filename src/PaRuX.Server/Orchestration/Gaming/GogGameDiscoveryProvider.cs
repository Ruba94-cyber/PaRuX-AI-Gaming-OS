using System.Security;
using Microsoft.Win32;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Read-only, bounded GOG local install discovery via registry metadata. No
/// credentials, Galaxy database edits, game execution or network requests.
/// </summary>
public sealed class GogGameDiscoveryProvider
{
    internal sealed record RegisteredGame(string Id, string Name, string InstallDirectory);
    private readonly Func<CancellationToken, IReadOnlyList<RegisteredGame>> _read;
    private const int MaxGames = 10000;

    public GogGameDiscoveryProvider() : this(ReadRegistry) { }

    internal GogGameDiscoveryProvider(Func<CancellationToken, IReadOnlyList<RegisteredGame>> read) =>
        _read = read ?? throw new ArgumentNullException(nameof(read));

    public Task<IReadOnlyList<DiscoveredGameDto>> DiscoverAsync(CancellationToken ct) =>
        Task.Run(() => Discover(ct), ct);

    internal IReadOnlyList<DiscoveredGameDto> Discover(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var games = new Dictionary<string, DiscoveredGameDto>(StringComparer.Ordinal);
        foreach (RegisteredGame entry in _read(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (games.Count >= MaxGames) break;
            if (!IsValidId(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 200 ||
                !IsValidInstallDirectory(entry.InstallDirectory))
                continue;
            games.TryAdd(entry.Id, new DiscoveredGameDto(
                "gog", entry.Id, entry.Name.Trim(), Path.GetFullPath(entry.InstallDirectory)));
        }
        return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 24 && id.All(char.IsAsciiDigit);

    private static bool IsValidInstallDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            string full = Path.GetFullPath(path);
            return Directory.Exists(full) &&
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    private static IReadOnlyList<RegisteredGame> ReadRegistry(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<RegisteredGame>();

        var found = new List<RegisteredGame>();
        // Windows registry views resolve WOW6432Node automatically. Never open
        // the literal WOW6432Node path and then redirect it a second time.
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= MaxGames) break;
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? gamesKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games", writable: false);
                if (gamesKey is null) continue;
                string[] ids = gamesKey.GetSubKeyNames();
                foreach (string id in ids.Take(MaxGames - found.Count))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsValidId(id)) continue;
                    try
                    {
                        using RegistryKey? entry = gamesKey.OpenSubKey(id, writable: false);
                        if (entry == null) continue;
                        string? path = entry.GetValue("PATH") as string;
                        string name = (entry.GetValue("GAMENAME") as string ??
                                       entry.GetValue("gameName") as string ?? id).Trim();
                        if (path != null) found.Add(new RegisteredGame(id, name, path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                    {
                        // Protected or malformed game entries do not block other games.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // One missing/protected registry hive cannot break a local Games page.
            }
        }
        return found;
    }
}
