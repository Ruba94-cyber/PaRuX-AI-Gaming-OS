using System.Security;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Read-only view of the local registry used by the extra store providers (fakeable in tests).</summary>
internal interface IStoreRegistry
{
    /// <summary>Sub-key names below <paramref name="path"/> (no hive prefix), unioned over HKLM/HKCU and both registry views.</summary>
    IReadOnlyList<string> SubKeys(string path);
    /// <summary>A string value, first match over HKLM then HKCU and both registry views; null when absent or not a string.</summary>
    string? GetString(string path, string name);
}

/// <summary>Read-only view of local files used by the extra store providers (fakeable in tests).</summary>
internal interface IStoreFiles
{
    /// <summary>Bounded text read of a regular local file; null when missing, linked, too large or unreadable.</summary>
    string? ReadText(string path, int maxBytes);
    /// <summary>A fully qualified, existing, local, non-linked directory.</summary>
    bool IsSafeDirectory(string path);
}

internal sealed class WindowsStoreRegistry : IStoreRegistry
{
    private static IEnumerable<RegistryKey> Roots()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? root = null;
            try { root = RegistryKey.OpenBaseKey(hive, view); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
            if (root is not null) yield return root;
        }
    }

    public IReadOnlyList<string> SubKeys(string path)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryKey root in Roots())
        {
            using (root)
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey(path, writable: false);
                    if (key is null) continue;
                    foreach (string name in key.GetSubKeyNames().Take(20_000)) names.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }
        return names.ToArray();
    }

    public string? GetString(string path, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (RegistryKey root in Roots())
        {
            using (root)
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey(path, writable: false);
                    if (key?.GetValue(name) is string value && value.Length > 0) return value;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }
        return null;
    }
}

internal sealed class WindowsStoreFiles : IStoreFiles
{
    public string? ReadText(string path, int maxBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(path).Length > maxBytes)
                return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SecurityException) { return null; }
    }

    public bool IsSafeDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            string full = Path.GetFullPath(path);
            return Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SecurityException) { return false; }
    }
}

/// <summary>One extra launcher: read-only discovery plus the official URI protocol its client registers.</summary>
public interface IExtraStoreProvider
{
    string Id { get; }
    Task<IReadOnlyList<DiscoveredGameDto>> DiscoverAsync(CancellationToken ct);
    /// <summary>The official protocol URI for an installed game, or false when the id is not launchable. Never an executable.</summary>
    bool TryBuildLaunchUri(DiscoveredGameDto game, out string uri);
}

/// <summary>Shared plumbing: bounded, deduplicated, validated, sorted.</summary>
public abstract class ExtraStoreProviderBase : IExtraStoreProvider
{
    internal const int MaxGames = 2000;
    internal const int MaxXmlBytes = 512 * 1024;

    internal sealed record Raw(string Id, string Name, string InstallDirectory);

    public abstract string Id { get; }
    private protected abstract IReadOnlyList<Raw> Read(CancellationToken ct);
    private protected IStoreFiles Files { get; }
    private protected IStoreRegistry Registry { get; }

    private protected ExtraStoreProviderBase(IStoreRegistry registry, IStoreFiles files)
    {
        Registry = registry;
        Files = files;
    }

    public Task<IReadOnlyList<DiscoveredGameDto>> DiscoverAsync(CancellationToken ct) => Task.Run(() => Discover(ct), ct);

    internal IReadOnlyList<DiscoveredGameDto> Discover(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var found = new Dictionary<string, DiscoveredGameDto>(StringComparer.OrdinalIgnoreCase);
        foreach (Raw raw in Read(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= MaxGames) break;
            string name = (raw.Name ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(raw.Id) || raw.Id.Length > 200 || raw.Id.Any(char.IsControl) ||
                name.Length is 0 or > 200 || name.Any(char.IsControl) || !Files.IsSafeDirectory(raw.InstallDirectory))
                continue;
            found.TryAdd(raw.Id, new DiscoveredGameDto(Id, raw.Id, name, Path.GetFullPath(raw.InstallDirectory),
                GameInstallStates.Installed));
        }
        return found.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public abstract bool TryBuildLaunchUri(DiscoveredGameDto game, out string uri);

    /// <summary>XML without DTDs or external resolution, size bounded by the caller.</summary>
    internal static XDocument? ParseXml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
            return XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
    }
}

/// <summary>EA app / Origin. Registry install dirs; the offer id (needed for the official origin2:// launch) comes from the game's own installerdata.xml when present.</summary>
public sealed class EaAppGameDiscoveryProvider : ExtraStoreProviderBase
{
    private static readonly Regex OfferId = new(@"^(Origin\.OFR\.\d{1,3}\.\d{1,10}|OFB-[A-Z]{2,8}:\d{1,12})$", RegexOptions.CultureInvariant);
    public override string Id => "ea";
    public EaAppGameDiscoveryProvider() : this(new WindowsStoreRegistry(), new WindowsStoreFiles()) { }
    internal EaAppGameDiscoveryProvider(IStoreRegistry registry, IStoreFiles files) : base(registry, files) { }

    internal static bool IsOfferId(string? id) => id is not null && OfferId.IsMatch(id);

    private protected override IReadOnlyList<Raw> Read(CancellationToken ct)
    {
        var list = new List<Raw>();
        foreach (string root in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts" })
        foreach (string name in Registry.SubKeys(root))
        {
            ct.ThrowIfCancellationRequested();
            string key = root + "\\" + name;
            string? dir = Registry.GetString(key, "Install Dir") ?? Registry.GetString(key, "InstallDir");
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string id = OfferIdFromInstallerData(Files.ReadText(Path.Combine(dir, "__Installer", "installerdata.xml"), MaxXmlBytes))
                ?? "name:" + Slug(name);
            list.Add(new Raw(id, name, dir));
        }
        return list;
    }

    internal static string Slug(string name) =>
        new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(80).ToArray()).ToLowerInvariant();

    internal static string? OfferIdFromInstallerData(string? xml)
    {
        XDocument? doc = ParseXml(xml);
        return doc?.Descendants().Where(e => e.Name.LocalName == "contentID")
            .Select(e => e.Value.Trim()).FirstOrDefault(IsOfferId);
    }

    public override bool TryBuildLaunchUri(DiscoveredGameDto game, out string uri)
    {
        uri = string.Empty;
        if (game.ProviderId != "ea" || !IsOfferId(game.ProviderGameId)) return false;
        uri = "origin2://game/launch/?offerIds=" + Uri.EscapeDataString(game.ProviderGameId);
        return true;
    }
}

/// <summary>Battle.net. Blizzard game uninstall entries carry <c>--uid=&lt;product code&gt;</c>, the code of the official battlenet:// protocol.</summary>
public sealed class BattleNetGameDiscoveryProvider : ExtraStoreProviderBase
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly Regex Uid = new(@"--uid=([a-z0-9_]{2,24})(?=\s|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Code = new(@"^[a-z0-9_]{2,24}$", RegexOptions.CultureInvariant);
    public override string Id => "battlenet";
    public BattleNetGameDiscoveryProvider() : this(new WindowsStoreRegistry(), new WindowsStoreFiles()) { }
    internal BattleNetGameDiscoveryProvider(IStoreRegistry registry, IStoreFiles files) : base(registry, files) { }

    internal static string? ProductCode(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString) || uninstallString.Length > 2048 ||
            !uninstallString.Contains("Blizzard", StringComparison.OrdinalIgnoreCase))
            return null;
        Match m = Uid.Match(uninstallString);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private protected override IReadOnlyList<Raw> Read(CancellationToken ct)
    {
        var list = new List<Raw>();
        foreach (string name in Registry.SubKeys(Uninstall))
        {
            ct.ThrowIfCancellationRequested();
            string key = Uninstall + "\\" + name;
            string? code = ProductCode(Registry.GetString(key, "UninstallString"));
            string? dir = Registry.GetString(key, "InstallLocation");
            string? title = Registry.GetString(key, "DisplayName");
            if (code is null || string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new Raw(code, title, dir));
        }
        return list;
    }

    public override bool TryBuildLaunchUri(DiscoveredGameDto game, out string uri)
    {
        uri = string.Empty;
        if (game.ProviderId != "battlenet" || !Code.IsMatch(game.ProviderGameId ?? string.Empty)) return false;
        uri = "battlenet://" + game.ProviderGameId;
        return true;
    }
}

/// <summary>Ubisoft Connect. <c>Ubisoft\Launcher\Installs\&lt;id&gt;\InstallDir</c>; the title comes from the install folder name (the registry carries none).</summary>
public sealed class UbisoftConnectGameDiscoveryProvider : ExtraStoreProviderBase
{
    private const string Installs = @"SOFTWARE\Ubisoft\Launcher\Installs";
    public override string Id => "ubisoft";
    public UbisoftConnectGameDiscoveryProvider() : this(new WindowsStoreRegistry(), new WindowsStoreFiles()) { }
    internal UbisoftConnectGameDiscoveryProvider(IStoreRegistry registry, IStoreFiles files) : base(registry, files) { }

    internal static bool IsGameId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 12 && id.All(char.IsAsciiDigit);

    private protected override IReadOnlyList<Raw> Read(CancellationToken ct)
    {
        var list = new List<Raw>();
        foreach (string id in Registry.SubKeys(Installs))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsGameId(id)) continue;
            string? dir = Registry.GetString(Installs + "\\" + id, "InstallDir");
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string trimmed = dir.TrimEnd('\\', '/');
            string name = trimmed.Length == 0 ? string.Empty : Path.GetFileName(trimmed);
            list.Add(new Raw(id, name, dir));
        }
        return list;
    }

    public override bool TryBuildLaunchUri(DiscoveredGameDto game, out string uri)
    {
        uri = string.Empty;
        if (game.ProviderId != "ubisoft" || !IsGameId(game.ProviderGameId)) return false;
        uri = "uplay://launch/" + game.ProviderGameId + "/0";
        return true;
    }
}

/// <summary>
/// Xbox app / Microsoft Store games. Package registry entries whose root lies under an XboxGames folder (or that
/// carry a MicrosoftGame.Config); the activation id is <c>PackageFamilyName!ApplicationId</c> from the readable appxmanifest.xml.
/// Protected WindowsApps packages whose manifest cannot be read are listed only if they have a game config; never launchable without an id.
/// </summary>
public sealed class XboxGameDiscoveryProvider : ExtraStoreProviderBase
{
    private const string Packages = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private static readonly Regex Aumid = new(@"^[A-Za-z0-9.\-]{3,64}_[a-z0-9]{13}![A-Za-z0-9.\-_]{1,64}$", RegexOptions.CultureInvariant);
    public override string Id => "xbox";
    public XboxGameDiscoveryProvider() : this(new WindowsStoreRegistry(), new WindowsStoreFiles()) { }
    internal XboxGameDiscoveryProvider(IStoreRegistry registry, IStoreFiles files) : base(registry, files) { }

    internal static bool IsAumid(string? id) => id is not null && Aumid.IsMatch(id);

    /// <summary>"Name_1.2.3.4_x64__publisherid" -> "Name_publisherid".</summary>
    internal static string? FamilyName(string packageFullName)
    {
        int split = packageFullName.IndexOf("__", StringComparison.Ordinal);
        int firstUnderscore = packageFullName.IndexOf('_');
        if (split <= 0 || firstUnderscore <= 0 || firstUnderscore > split) return null;
        string publisher = packageFullName[(split + 2)..];
        return publisher.Length == 13 ? packageFullName[..firstUnderscore] + "_" + publisher : null;
    }

    internal sealed record Manifest(string IdentityName, string ApplicationId, string? DisplayName);

    internal static Manifest? ParseManifest(string? xml)
    {
        XDocument? doc = ParseXml(xml);
        if (doc?.Root is null) return null;
        string? identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
        string? app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application")?.Attribute("Id")?.Value;
        string? display = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(app)) return null;
        if (display is not null && (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || display.Length == 0))
            display = null;
        return new Manifest(identity, app, display);
    }

    internal static string? ConfigDisplayName(string? xml)
    {
        XDocument? doc = ParseXml(xml);
        string? name = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value.Trim();
        return string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    private protected override IReadOnlyList<Raw> Read(CancellationToken ct)
    {
        var list = new List<Raw>();
        foreach (string full in Registry.SubKeys(Packages))
        {
            ct.ThrowIfCancellationRequested();
            string? family = FamilyName(full);
            string key = Packages + "\\" + full;
            string? root = Registry.GetString(key, "PackageRootFolder") ?? Registry.GetString(key, "Path");
            if (family is null || string.IsNullOrWhiteSpace(root)) continue;
            string? config = Files.ReadText(Path.Combine(root, "MicrosoftGame.Config"), MaxXmlBytes);
            bool underXboxGames = root.Contains(@"\XboxGames\", StringComparison.OrdinalIgnoreCase);
            if (!underXboxGames && config is null) continue; // not recognisably a game: skip ordinary Store apps
            Manifest? manifest = ParseManifest(Files.ReadText(Path.Combine(root, "AppxManifest.xml"), MaxXmlBytes));
            if (manifest is null) continue; // no readable activation id: nothing honest to list or launch
            string id = family + "!" + manifest.ApplicationId;
            if (!IsAumid(id)) continue;
            string? folderName = Path.GetFileName(Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? string.Empty);
            string name = ConfigDisplayName(config) ?? manifest.DisplayName
                ?? (underXboxGames && !string.IsNullOrWhiteSpace(folderName) ? folderName : manifest.IdentityName);
            list.Add(new Raw(id, name, root));
        }
        return list;
    }

    public override bool TryBuildLaunchUri(DiscoveredGameDto game, out string uri)
    {
        uri = string.Empty;
        if (game.ProviderId != "xbox" || !IsAumid(game.ProviderGameId)) return false;
        uri = @"shell:AppsFolder\" + game.ProviderGameId;
        return true;
    }
}

/// <summary>The four read-only extra launchers behind one registration; discovery and launch both go through here.</summary>
public sealed class ExtraStoreCatalog
{
    public IReadOnlyList<IExtraStoreProvider> Providers { get; }

    public ExtraStoreCatalog() : this(new IExtraStoreProvider[]
    {
        new EaAppGameDiscoveryProvider(), new BattleNetGameDiscoveryProvider(),
        new UbisoftConnectGameDiscoveryProvider(), new XboxGameDiscoveryProvider(),
    }) { }

    public ExtraStoreCatalog(IReadOnlyList<IExtraStoreProvider> providers) => Providers = providers;

    public IExtraStoreProvider? Find(string? providerId) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>All extra providers, one failing provider never hides the others.</summary>
    public async Task<IReadOnlyList<DiscoveredGameDto>> DiscoverAllAsync(CancellationToken ct)
    {
        var all = new List<DiscoveredGameDto>();
        foreach (IExtraStoreProvider provider in Providers)
        {
            ct.ThrowIfCancellationRequested();
            try { all.AddRange(await provider.DiscoverAsync(ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                SecurityException or XmlException) { }
        }
        return all;
    }
}
