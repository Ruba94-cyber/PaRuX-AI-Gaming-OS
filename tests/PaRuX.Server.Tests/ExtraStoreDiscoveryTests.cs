using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExtraStoreDiscoveryTests
{
    private sealed class FakeRegistry : IStoreRegistry
    {
        public readonly Dictionary<string, List<string>> Keys = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<(string Path, string Name), string> Values = new();

        public FakeRegistry Add(string parent, string child, params (string Name, string Value)[] values)
        {
            if (!Keys.TryGetValue(parent, out var list)) Keys[parent] = list = new List<string>();
            list.Add(child);
            foreach (var (n, v) in values) Values[(parent + "\\" + child, n)] = v;
            return this;
        }

        public IReadOnlyList<string> SubKeys(string path) => Keys.TryGetValue(path, out var l) ? l : Array.Empty<string>();
        public string? GetString(string path, string name) => Values.TryGetValue((path, name), out var v) ? v : null;
    }

    private sealed class FakeFiles : IStoreFiles
    {
        public readonly Dictionary<string, string> Text = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public string? ReadText(string path, int maxBytes) => Text.TryGetValue(path, out var t) && t.Length <= maxBytes ? t : null;
        public bool IsSafeDirectory(string path) => Dirs.Contains(path);
    }

    // ---- EA app ----

    private const string EaRoot = @"SOFTWARE\EA Games";

    [Fact]
    public void Ea_reads_install_dir_and_offer_id_from_installerdata()
    {
        var reg = new FakeRegistry().Add(EaRoot, "Test Racer", ("Install Dir", @"C:\Games\EA\Test Racer"));
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Games\EA\Test Racer");
        files.Text[@"C:\Games\EA\Test Racer\__Installer\installerdata.xml"] =
            "<game><contentIDs><contentID>Origin.OFR.50.0001234</contentID></contentIDs></game>";

        var game = Assert.Single(new EaAppGameDiscoveryProvider(reg, files).Discover(CancellationToken.None));

        Assert.Equal("ea", game.ProviderId);
        Assert.Equal("Origin.OFR.50.0001234", game.ProviderGameId);
        Assert.Equal("Test Racer", game.Name);
        Assert.Equal(GameInstallStates.Installed, game.InstallState);
    }

    [Fact]
    public void Ea_without_offer_id_is_listed_but_not_launchable()
    {
        var reg = new FakeRegistry().Add(EaRoot, "Old Game", ("InstallDir", @"C:\G\Old"));
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\G\Old");
        var provider = new EaAppGameDiscoveryProvider(reg, files);

        var game = Assert.Single(provider.Discover(CancellationToken.None));

        Assert.False(provider.TryBuildLaunchUri(game, out _));
        Assert.False(GameStoreLaunchIdPolicy.SupportsOfficialLaunch("ea", game.ProviderGameId));
    }

    [Fact]
    public void Ea_launch_uri_is_the_official_protocol_with_an_escaped_offer_id()
    {
        var provider = new EaAppGameDiscoveryProvider(new FakeRegistry(), new FakeFiles());
        Assert.True(provider.TryBuildLaunchUri(new DiscoveredGameDto("ea", "OFB-EAST:109552316", "x", @"C:\x"), out string uri));
        Assert.Equal("origin2://game/launch/?offerIds=OFB-EAST%3A109552316", uri);
        Assert.False(provider.TryBuildLaunchUri(new DiscoveredGameDto("ea", "x&cmd=calc", "x", @"C:\x"), out _));
    }

    [Fact]
    public void Ea_installerdata_with_dtd_or_garbage_is_ignored()
    {
        Assert.Null(EaAppGameDiscoveryProvider.OfferIdFromInstallerData("<!DOCTYPE a [<!ENTITY x SYSTEM \"file:///c:/x\">]><a>&x;</a>"));
        Assert.Null(EaAppGameDiscoveryProvider.OfferIdFromInstallerData("not xml"));
        Assert.Null(EaAppGameDiscoveryProvider.OfferIdFromInstallerData("<game><contentID>../../evil</contentID></game>"));
    }

    // ---- Battle.net ----

    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [Fact]
    public void BattleNet_reads_product_code_from_the_blizzard_uninstaller_command()
    {
        var reg = new FakeRegistry()
            .Add(Uninstall, "Game A",
                ("UninstallString", "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=wow --displayname=\"Game A\""),
                ("InstallLocation", @"C:\Games\Blizzard\A"), ("DisplayName", "Game A"))
            .Add(Uninstall, "Other App",
                ("UninstallString", "\"C:\\Tools\\unins000.exe\" --uid=wow"),
                ("InstallLocation", @"C:\Tools"), ("DisplayName", "Other App"))
            .Add(Uninstall, "No Location",
                ("UninstallString", "\"C:\\x\\Blizzard Uninstaller.exe\" --uid=d3"), ("DisplayName", "No Location"));
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Games\Blizzard\A");
        files.Dirs.Add(@"C:\Tools");
        var provider = new BattleNetGameDiscoveryProvider(reg, files);

        var game = Assert.Single(provider.Discover(CancellationToken.None));

        Assert.Equal("wow", game.ProviderGameId);
        Assert.True(provider.TryBuildLaunchUri(game, out string uri));
        Assert.Equal("battlenet://wow", uri);
    }

    [Theory]
    [InlineData("wow", true)]
    [InlineData("hs_beta", true)]
    [InlineData("WoW", false)]
    [InlineData("w", false)]
    [InlineData("wow/../x", false)]
    [InlineData("wow --exec=calc", false)]
    public void BattleNet_codes_are_strictly_validated(string code, bool expected) =>
        Assert.Equal(expected, GameStoreLaunchIdPolicy.BattleNet(code));

    // ---- Ubisoft Connect ----

    private const string Installs = @"SOFTWARE\Ubisoft\Launcher\Installs";

    [Fact]
    public void Ubisoft_lists_numeric_installs_named_after_their_folder_and_builds_uplay_uri()
    {
        var reg = new FakeRegistry()
            .Add(Installs, "720", ("InstallDir", @"C:\Games\Ubisoft\Test Quest\"))
            .Add(Installs, "not-numeric", ("InstallDir", @"C:\Games\Bad"))
            .Add(Installs, "721", ("InstallDir", @"C:\Games\Missing"));
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Games\Ubisoft\Test Quest\");
        var provider = new UbisoftConnectGameDiscoveryProvider(reg, files);

        var game = Assert.Single(provider.Discover(CancellationToken.None));

        Assert.Equal("720", game.ProviderGameId);
        Assert.Equal("Test Quest", game.Name);
        Assert.True(provider.TryBuildLaunchUri(game, out string uri));
        Assert.Equal("uplay://launch/720/0", uri);
        Assert.False(provider.TryBuildLaunchUri(game with { ProviderGameId = "720/../1" }, out _));
    }

    // ---- Xbox / Microsoft Store ----

    private const string Packages = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string Manifest =
        "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
        "<Identity Name=\"Contoso.TestAdventure\" Publisher=\"CN=Contoso\" Version=\"1.0.0.0\"/>" +
        "<Properties><DisplayName>ms-resource:AppName</DisplayName></Properties>" +
        "<Applications><Application Id=\"Game\" Executable=\"game.exe\"/></Applications></Package>";

    [Fact]
    public void Xbox_lists_xboxgames_packages_with_activation_id_and_uses_the_shell_appsfolder_moniker()
    {
        string root = @"D:\XboxGames\Test Adventure\Content";
        var reg = new FakeRegistry()
            .Add(Packages, "Contoso.TestAdventure_1.0.0.0_x64__abcdefghijklm", ("PackageRootFolder", root))
            .Add(Packages, "Some.StoreApp_1.0.0.0_x64__abcdefghijklm", ("PackageRootFolder", @"C:\Program Files\WindowsApps\Some.StoreApp"))
            .Add(Packages, "Broken_name", ("PackageRootFolder", root));
        var files = new FakeFiles();
        files.Dirs.Add(root);
        files.Text[root + @"\AppxManifest.xml"] = Manifest;
        files.Text[root + @"\MicrosoftGame.Config"] =
            "<Game><ShellVisuals DefaultDisplayName=\"Test Adventure\"/></Game>";
        var provider = new XboxGameDiscoveryProvider(reg, files);

        var game = Assert.Single(provider.Discover(CancellationToken.None));

        Assert.Equal("Contoso.TestAdventure_abcdefghijklm!Game", game.ProviderGameId);
        Assert.Equal("Test Adventure", game.Name);
        Assert.True(provider.TryBuildLaunchUri(game, out string uri));
        Assert.Equal(@"shell:AppsFolder\Contoso.TestAdventure_abcdefghijklm!Game", uri);
    }

    [Fact]
    public void Xbox_without_readable_manifest_is_not_listed_and_name_falls_back_to_the_folder()
    {
        string root = @"D:\XboxGames\Fallback Name\Content";
        var reg = new FakeRegistry().Add(Packages, "Contoso.Fb_1.0.0.0_x64__abcdefghijklm", ("PackageRootFolder", root));
        var files = new FakeFiles();
        files.Dirs.Add(root);
        Assert.Empty(new XboxGameDiscoveryProvider(reg, files).Discover(CancellationToken.None));

        files.Text[root + @"\AppxManifest.xml"] = Manifest;
        Assert.Equal("Fallback Name", Assert.Single(new XboxGameDiscoveryProvider(reg, files).Discover(CancellationToken.None)).Name);
    }

    [Theory]
    [InlineData("Contoso.TestAdventure_1.0.0.0_x64__abcdefghijklm", "Contoso.TestAdventure_abcdefghijklm")]
    [InlineData("Bad_1.0.0.0_x64__short", null)]
    [InlineData("nounderscores", null)]
    public void Xbox_family_name_is_derived_from_the_package_full_name(string full, string? expected) =>
        Assert.Equal(expected, XboxGameDiscoveryProvider.FamilyName(full));

    [Theory]
    [InlineData("Contoso.TestAdventure_abcdefghijklm!Game", true)]
    [InlineData("Contoso.TestAdventure_abcdefghijklm!Game --evil", false)]
    [InlineData(@"..\x_abcdefghijklm!Game", false)]
    [InlineData("Contoso_short!Game", false)]
    public void Xbox_activation_ids_are_strictly_validated(string id, bool expected) =>
        Assert.Equal(expected, GameStoreLaunchIdPolicy.Xbox(id));

    // ---- shared behaviour ----

    [Fact]
    public void Entries_with_unsafe_or_missing_install_directories_are_dropped()
    {
        var reg = new FakeRegistry().Add(Installs, "5", ("InstallDir", @"\\server\share\Game"));
        var files = new FakeFiles(); // nothing registered as a safe directory
        Assert.Empty(new UbisoftConnectGameDiscoveryProvider(reg, files).Discover(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UbisoftConnectGameDiscoveryProvider(new FakeRegistry(), new FakeFiles()).DiscoverAsync(cts.Token));
    }

    [Fact]
    public async Task Catalog_merges_providers_and_one_failure_does_not_hide_the_rest()
    {
        var good = new UbisoftConnectGameDiscoveryProvider(
            new FakeRegistry().Add(Installs, "9", ("InstallDir", @"C:\G\Nine")), new FakeFiles { Dirs = { @"C:\G\Nine" } });
        var catalog = new ExtraStoreCatalog(new IExtraStoreProvider[] { new Throwing(), good });

        var all = await catalog.DiscoverAllAsync(CancellationToken.None);

        Assert.Equal("9", Assert.Single(all).ProviderGameId);
        Assert.Same(good, catalog.Find("UBISOFT"));
        Assert.Null(catalog.Find("steam"));
    }

    private sealed class Throwing : IExtraStoreProvider
    {
        public string Id => "boom";
        public Task<IReadOnlyList<DiscoveredGameDto>> DiscoverAsync(CancellationToken ct) => throw new IOException("denied");
        public bool TryBuildLaunchUri(DiscoveredGameDto game, out string uri) { uri = string.Empty; return false; }
    }

    [Fact]
    public void New_sources_join_the_title_catalogue_after_the_existing_stores()
    {
        var discovered = new[]
        {
            new DiscoveredGameDto("ubisoft", "720", "Shared Name", @"C:\a", GameInstallStates.Installed),
            new DiscoveredGameDto("steam", "10", "Shared Name", @"C:\b", GameInstallStates.Installed),
        };
        var title = Assert.Single(GameTitleCatalog.Build(discovered, Array.Empty<ManualGameDto>(),
            new Dictionary<string, GameTitleState>()));
        Assert.Equal(new[] { "steam", "ubisoft" }, title.Installations.Select(i => i.Source).ToArray());
    }
}
