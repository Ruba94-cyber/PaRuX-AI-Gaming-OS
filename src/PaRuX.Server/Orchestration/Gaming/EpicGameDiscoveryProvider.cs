using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Read-only discovery of Epic Games Launcher's local installation manifests.
/// Does not authenticate, access cloud accounts, install, modify, or execute any game.
/// </summary>
public sealed class EpicGameDiscoveryProvider
{
    private const int MaxGames = 10000;
    private const long MaxManifestBytes = 512 * 1024;
    private readonly string _manifestsDirectory;

    public EpicGameDiscoveryProvider() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests")) { }

    public EpicGameDiscoveryProvider(string manifestsDirectory) =>
        _manifestsDirectory = Path.GetFullPath(manifestsDirectory);

    public Task<IReadOnlyList<DiscoveredGameDto>> DiscoverAsync(CancellationToken ct) =>
        Task.Run(() => Discover(ct), ct);

    internal IReadOnlyList<DiscoveredGameDto> Discover(CancellationToken ct)
    {
        if (!Directory.Exists(_manifestsDirectory)) return Array.Empty<DiscoveredGameDto>();
        var found = new Dictionary<string, DiscoveredGameDto>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_manifestsDirectory, "*.item", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<DiscoveredGameDto>(); }

        try
        {
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (found.Count >= MaxGames) break;
                try
                {
                    if (!File.Exists(file) || new FileInfo(file).Length > MaxManifestBytes) continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    string? id = ReadString(root, "AppName");
                    string? name = ReadString(root, "DisplayName");
                    string? path = ReadString(root, "InstallLocation");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(path) || id.Length > 200 || name.Length > 200 ||
                        path.Length > 1024 || !Path.IsPathFullyQualified(path) ||
                        path.StartsWith(@"\\", StringComparison.Ordinal) ||
                        !Directory.Exists(path))
                        continue;
                    if (root.TryGetProperty("bIsIncompleteInstall", out JsonElement incomplete)
                        && incomplete.ValueKind == JsonValueKind.True) continue;

                    found.TryAdd(id, new DiscoveredGameDto("epic", id, name, Path.GetFullPath(path)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    // One corrupt manifest must not hide the remaining installed games.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
}
