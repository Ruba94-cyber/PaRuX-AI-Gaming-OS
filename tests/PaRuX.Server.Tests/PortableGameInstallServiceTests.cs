using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Downloads;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Offline synthetic portable archives; no untrusted executable is ever launched.
/// Test data consists solely of deliberately inert .exe-named byte strings.
/// </summary>
public sealed class PortableGameInstallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-portable-deploy-" + Guid.NewGuid().ToString("N"));
    private bool _gaming;

    private string ZipPath => Path.Combine(_root, "fixture.zip");
    private string InstallRoot => Path.Combine(_root, "managed");
    private string LibraryPath => Path.Combine(_root, "manual-games.json");
    private ManualGameLibrary Library => new(LibraryPath);
    private PortableGameInstallService NewService() =>
        new(Library, () => _gaming, () => CancellationToken.None, InstallRoot);

    public PortableGameInstallServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { }
    }

    private string CreateZip(params (string Path, string Body)[] files)
    {
        using var stream = File.Create(ZipPath);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string body) in files)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(body);
            }
        }
        return ZipPath;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public async Task Verified_portable_zip_extracts_registers_and_never_runs_executable()
    {
        string zip = CreateZip(("bin/Fixture.exe", "inert fake exe: cannot run"),
            ("data/readme.txt", "public domain synthetic fixture"));
        string before = Sha(zip);
        var result = await NewService().InstallAsync(zip, before, "bin/Fixture.exe",
            "Fixture Game", CancellationToken.None);
        Assert.Equal("Fixture Game", result.Name);
        Assert.Equal(ManualEntryKinds.Game, result.Kind);
        Assert.Equal("PC", result.Platform);
        Assert.Equal("inert fake exe: cannot run", File.ReadAllText(result.ExecutablePath).TrimStart('\uFEFF'));
        Assert.Single(Library.List());
        Assert.Equal(result.Id, Assert.Single(new ManualGameLibrary(LibraryPath).List()).Id);
        Assert.Equal(before, Sha(zip)); // original download must be preserved exactly
        Assert.True(result.ExecutablePath.StartsWith(InstallRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetDirectories(InstallRoot, "*.partial"));
    }

    [Fact]
    public async Task A_wrong_independent_sha_refuses_before_creating_any_install()
    {
        string zip = CreateZip(("bin/Fixture.exe", "data"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewService().InstallAsync(zip, new string('0', 64), "bin/Fixture.exe",
                "Fixture Game", CancellationToken.None));
        Assert.Empty(Library.List());
        Assert.DoesNotContain(Directory.GetDirectories(InstallRoot),
            p => Path.GetFileName(p).StartsWith("game-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/system/game.exe")]
    [InlineData("bin/../game.exe")]
    [InlineData("setup.exe")]
    [InlineData("bin/keygen.exe")]
    [InlineData("bin/install.exe")]
    [InlineData("bin/game.bat")]
    [InlineData("bin//game.exe")]
    [InlineData("\\\\server\\game.exe")]
    public void Invalid_or_installer_like_executable_paths_are_refused(string relative) =>
        Assert.False(PortableGameInstallService.ValidPortableEntryPoint(relative));

    [Fact]
    public async Task Missing_portable_executable_does_not_publish_game_or_modify_zip()
    {
        string zip = CreateZip(("docs/readme.txt", "synthetic"));
        string before = Sha(zip);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewService().InstallAsync(zip, before, "bin/Fixture.exe", "Game", CancellationToken.None));
        Assert.Empty(Library.List());
        Assert.Equal(before, Sha(zip));
        Assert.Empty(Directory.GetDirectories(InstallRoot));
    }

    [Fact]
    public async Task Duplicate_case_conflicting_entries_are_rejected_without_mutation()
    {
        string zip = CreateZip(("bin/Game.exe", "a"), ("bin/game.exe", "b"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewService().InstallAsync(zip, Sha(zip), "bin/Game.exe", "Game", CancellationToken.None));
        Assert.Empty(Library.List());
    }

    [Fact]
    public async Task Zip_entry_traversal_is_refused_and_cannot_escape_staging()
    {
        string zip = CreateZip(("../escape.txt", "inert"), ("bin/Game.exe", "data"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NewService().InstallAsync(zip, Sha(zip), "bin/Game.exe", "Game", CancellationToken.None));
        Assert.Empty(Library.List());
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Gaming_hard_off_refuses_without_altering_game_or_library()
    {
        string zip = CreateZip(("Game.exe", "inert"));
        _gaming = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewService().InstallAsync(zip, Sha(zip), "Game.exe", "Game", CancellationToken.None));
        Assert.Empty(Library.List());
        Assert.False(Directory.Exists(InstallRoot));
    }

    [Fact]
    public async Task Cancelled_request_does_not_create_an_installed_game()
    {
        string zip = CreateZip(("Game.exe", "inert"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewService().InstallAsync(zip, Sha(zip), "Game.exe", "Game", cts.Token));
        Assert.Empty(Library.List());
    }

    [Fact]
    public async Task A_second_install_while_the_lease_is_held_is_refused_honestly_without_raw_io_text()
    {
        string zip = CreateZip(("Game.exe", "inert"));
        Directory.CreateDirectory(InstallRoot);
        using var held = new FileStream(Path.Combine(InstallRoot, ".parux-portable.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var ex = await Assert.ThrowsAsync<PortableGameInstallService.PortableInstallBusyException>(() =>
            NewService().InstallAsync(zip, Sha(zip), "Game.exe", "Game", CancellationToken.None));
        Assert.DoesNotContain(".lock", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("process", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in progress", ex.Message);
        Assert.Empty(Library.List());
        Assert.Empty(Directory.GetDirectories(InstallRoot));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task Two_concurrent_installs_one_succeeds_the_other_is_busy_and_a_retry_then_succeeds()
    {
        // Two different synthetic archives against the real service and the real on-disk lease (no mock of the lease).
        // Install A is parked while it holds the lease; install B must report "busy" within its short wait and change nothing.
        string zipA = Path.Combine(_root, "a.zip"), zipB = Path.Combine(_root, "b.zip");
        WriteZip(zipA, ("A.exe", "inert A"));
        WriteZip(zipB, ("B.exe", "inert B"));
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new PortableGameInstallService(Library, () => false, () => CancellationToken.None, InstallRoot,
            afterLeaseAcquired: async ct => { holding.SetResult(); await release.Task.WaitAsync(ct); });
        var second = new PortableGameInstallService(Library, () => false, () => CancellationToken.None, InstallRoot,
            leaseWait: TimeSpan.FromMilliseconds(300));

        Task<ManualGameDto> a = first.InstallAsync(zipA, Sha(zipA), "A.exe", "Game A", CancellationToken.None);
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var busy = await Assert.ThrowsAsync<PortableGameInstallService.PortableInstallBusyException>(() =>
            second.InstallAsync(zipB, Sha(zipB), "B.exe", "Game B", CancellationToken.None));
        Assert.Contains("in progress", busy.Message);
        Assert.True(File.Exists(zipB)); // the busy install kept its archive
        Assert.Empty(Library.List());   // and registered nothing while A is still running

        release.SetResult();
        ManualGameDto installedA = await a.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("Game A", installedA.Name);

        // Retrying B once A has finished succeeds, so the busy state is recoverable and not sticky.
        ManualGameDto installedB = await second.InstallAsync(zipB, Sha(zipB), "B.exe", "Game B", CancellationToken.None);
        Assert.Equal("Game B", installedB.Name);
        Assert.Equal(new[] { "Game A", "Game B" }, Library.List().Select(g => g.Name).Order());
        Assert.NotEqual(installedA.ExecutablePath, installedB.ExecutablePath);
        Assert.Empty(Directory.GetDirectories(InstallRoot, "*.partial"));
    }

    private static void WriteZip(string path, params (string Path, string Body)[] files)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string entryPath, string body) in files)
        {
            var entry = zip.CreateEntry(entryPath, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(body);
        }
    }

    [Fact]
    public void Download_enqueuing_requires_opt_in_zip_and_independent_sha()
    {
        using var manager = new GameDownloadManager(new System.Net.Http.HttpClient(),
            () => false, _ => long.MaxValue, null, Array.Empty<TimeSpan>());
        Assert.Throws<GameDownloadException>(() => manager.Enqueue(
            new GameDownloadRequest("https://example.org/demo.zip", _root, "demo.zip",
                Kind: GameDownloadKinds.Http, AutoInstallPortable: true,
                PortableExecutableRelativePath: "Game.exe", PortableGameTitle: "Demo")));
        Assert.Empty(manager.List());
    }

    [Fact]
    public void No_user_consent_means_no_post_install_fields()
    {
        using var manager = new GameDownloadManager(new System.Net.Http.HttpClient(),
            () => false, _ => long.MaxValue, null, Array.Empty<TimeSpan>());
        Assert.Throws<GameDownloadException>(() => manager.Enqueue(
            new GameDownloadRequest("https://example.org/demo.zip", _root, "demo.zip",
                AutoInstallPortable: false, PortableExecutableRelativePath: "Game.exe")));
    }
}
