using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Pure inert files in isolated temp folders. This tests a ZIP-compatible
/// opt-in drop-in *adapter*, not that any particular game accepts the archive.
/// No game, mod loader, executable, GPU, network or installer is launched.
/// </summary>
public sealed class GameModDropInServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-mod-dropin-" + Guid.NewGuid().ToString("N"));
    private string Exports => Path.Combine(_root, "exported");
    private string Receipts => Path.Combine(_root, "receipts");
    private string Mods => Path.Combine(_root, "Synthetic Offline RPG", "mods");
    private string GameExe => Path.Combine(_root, "Synthetic Offline RPG", "SyntheticGame.exe");
    private bool _gaming;
    private bool _gameRunning;

    public GameModDropInServiceTests()
    {
        Directory.CreateDirectory(Exports);
        Directory.CreateDirectory(Receipts);
        Directory.CreateDirectory(Mods);
        File.WriteAllText(GameExe, "inert EXE fixture; never run");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private GameModDropInService Service() =>
        new(() => _gaming, Exports, Receipts, _ => _gameRunning);

    // A tiny harmless PNG with a standard IHDR. The adapter only validates
    // dimensions/header and ZIP SHA; raster decoding belongs to generation.
    private static byte[] InertPng()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9/xS8AAAAASUVORK5CYII=");
        return png;
    }

    private (string Path, string Sha) WriteBundle(
        string assetPath = "textures/armor.png", bool extraExecutable = false)
    {
        byte[] image = InertPng();
        string path = Path.Combine(Exports, "parux-game-texture-" +
            Guid.NewGuid().ToString("N") + ".zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var texture = zip.CreateEntry(assetPath);
            using (Stream dst = texture.Open()) dst.Write(image);
            var manifest = zip.CreateEntry(".parux/manifest.json");
            using (Stream dst = manifest.Open())
            {
                JsonSerializer.Serialize(dst, new
                {
                    formatVersion = 1, createdBy = "PaRuX",
                    kind = "png-overlay", title = "Synthetic Offline RPG",
                    relativeAssetPath = assetPath,
                    originalSha256 = new string('a', 64),
                    modifiedSha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
                    width = 1, height = 1, changedPixels = 1,
                    installation = "manual", hotReloadGuaranteed = false
                });
            }
            if (extraExecutable)
            {
                using Stream bad = zip.CreateEntry("autorun.exe").Open();
                bad.WriteByte(7);
            }
        }
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();
        return (path, sha);
    }

    private GameModDropInInstallRequest Request(string path, string sha, bool compatible = true,
        bool closed = true, string? folder = null) =>
        new(path, sha, folder ?? Mods, compatible, closed, GameExe);

    [Fact]
    public void Installs_only_a_fresh_named_zip_and_removes_after_restart_with_sha_proof()
    {
        var bundle = WriteBundle();
        File.WriteAllText(Path.Combine(Mods, "my-original-mod.zip"), "foreign fixture");
        var service = Service();
        Assert.Empty(service.List());
        GameModDropInReceiptDto item = service.Install(Request(bundle.Path, bundle.Sha));
        Assert.StartsWith(Mods, item.InstalledFile);
        Assert.Equal("installed", item.State);
        Assert.Equal(GameExe, item.GameExecutablePath);
        Assert.EndsWith(".zip", item.InstalledFile);
        Assert.Equal(File.ReadAllBytes(bundle.Path), File.ReadAllBytes(item.InstalledFile));
        Assert.Equal("foreign fixture", File.ReadAllText(Path.Combine(Mods, "my-original-mod.zip")));
        Assert.Single(service.List());

        // A fresh Core instance can still see its durable receipt.
        var restarted = Service();
        Assert.Equal(item.Id, Assert.Single(restarted.List()).Id);
        Assert.True(restarted.Remove(new(item.Id, Consent: true, OfflineGameClosedConfirmed: true)));
        Assert.False(File.Exists(item.InstalledFile));
        Assert.True(File.Exists(bundle.Path)); // owner export isn't deleted
        Assert.Equal("foreign fixture", File.ReadAllText(Path.Combine(Mods, "my-original-mod.zip")));
        Assert.Empty(restarted.List());
    }

    [Fact]
    public void A_running_or_paused_game_blocks_install_and_later_removal_even_with_consent()
    {
        var bundle = WriteBundle();
        _gameRunning = true;
        Assert.Throws<InvalidOperationException>(() =>
            Service().Install(Request(bundle.Path, bundle.Sha)));
        Assert.Empty(Service().List());
        Assert.Empty(Directory.EnumerateFiles(Mods));

        _gameRunning = false;
        var installed = Service().Install(Request(bundle.Path, bundle.Sha));
        _gameRunning = true;
        Assert.Throws<InvalidOperationException>(() =>
            Service().Remove(new(installed.Id, true, true)));
        Assert.True(File.Exists(installed.InstalledFile));
        Assert.Single(Service().List());

        _gameRunning = false;
        Assert.True(Service().Remove(new(installed.Id, true, true)));
        Assert.False(File.Exists(installed.InstalledFile));
    }

    [Fact]
    public void Game_relaunch_between_preflight_and_publish_refuses_ZIP_commit()
    {
        var bundle = WriteBundle();
        int checks = 0;
        var service = new GameModDropInService(() => false, Exports, Receipts,
            _ => ++checks >= 2); // closed during request admission, running before rename
        Assert.Throws<InvalidOperationException>(() =>
            service.Install(Request(bundle.Path, bundle.Sha)));
        Assert.True(checks >= 2);
        Assert.Empty(Directory.EnumerateFiles(Mods));
        // A prewrite crash/relaunch receipt is deliberate, recoverable and
        // does not itself prove a mod was ever installed.
        var pending = Assert.Single(Service().List());
        Assert.Equal("missing", pending.State);
        _gameRunning = false;
        Assert.True(Service().Remove(new(pending.Id, true, true)));
        Assert.Empty(Service().List());
    }

    [Fact]
    public void Game_relaunch_before_remove_commit_keeps_the_original_ZIP_and_receipt()
    {
        var bundle = WriteBundle();
        var installed = Service().Install(Request(bundle.Path, bundle.Sha));
        int checks = 0;
        var service = new GameModDropInService(() => false, Exports, Receipts,
            _ => ++checks >= 2);
        Assert.Throws<InvalidOperationException>(() =>
            service.Remove(new(installed.Id, true, true)));
        Assert.True(checks >= 2);
        Assert.True(File.Exists(installed.InstalledFile));
        Assert.Equal("installed", Assert.Single(Service().List()).State);
    }

    [Fact]
    public void Missing_game_executable_or_wrong_executable_is_refused_without_any_game_write()
    {
        var bundle = WriteBundle();
        Assert.Throws<ArgumentException>(() => Service().Install(
            Request(bundle.Path, bundle.Sha) with { GameExecutablePath = "" }));
        Assert.Throws<ArgumentException>(() => Service().Install(
            Request(bundle.Path, bundle.Sha) with { GameExecutablePath = Path.Combine(_root, "launcher.lnk") }));
        Assert.Throws<InvalidOperationException>(() => Service().Install(
            Request(bundle.Path, bundle.Sha) with { GameExecutablePath = Path.Combine(_root, "missing.exe") }));
        Assert.Empty(Directory.EnumerateFiles(Mods));
        Assert.Empty(Service().List());
    }

    [Fact]
    public void Older_unbound_receipt_is_visible_but_cannot_authorize_deletion()
    {
        var bundle = WriteBundle();
        var installed = Service().Install(Request(bundle.Path, bundle.Sha));
        string receiptPath = Path.Combine(Receipts, "parux-mod-" + installed.Id + ".json");
        string text = File.ReadAllText(receiptPath);
        using var document = JsonDocument.Parse(text);
        var props = document.RootElement.EnumerateObject()
            .Where(x => x.Name != "GameExecutablePath")
            .ToDictionary(x => x.Name, x => x.Value.Clone());
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(props));

        var unbound = Assert.Single(Service().List());
        Assert.Equal("unbound", unbound.State);
        Assert.Throws<ArgumentException>(() =>
            Service().Remove(new(installed.Id, true, true)));
        Assert.True(File.Exists(installed.InstalledFile));
        Assert.Single(Service().List());
    }

    [Fact]
    public void A_user_modified_mod_ZIP_is_never_deleted_and_keeps_receipt()
    {
        var bundle = WriteBundle();
        var item = Service().Install(Request(bundle.Path, bundle.Sha));
        File.AppendAllText(item.InstalledFile, "a-user-modified-this-file");
        Assert.Equal("changed", Assert.Single(Service().List()).State);
        Assert.Throws<InvalidOperationException>(() =>
            Service().Remove(new(item.Id, true, true)));
        Assert.True(File.Exists(item.InstalledFile));
        Assert.Single(Service().List());
    }

    [Fact]
    public void Missing_installed_ZIP_is_recoverable_without_deleting_other_files()
    {
        var bundle = WriteBundle();
        var item = Service().Install(Request(bundle.Path, bundle.Sha));
        File.Delete(item.InstalledFile);
        Assert.Equal("missing", Assert.Single(Service().List()).State);
        Assert.True(Service().Remove(new(item.Id, true, true)));
        Assert.Empty(Service().List());
        Assert.True(File.Exists(bundle.Path));
    }

    [Fact]
    public void Game_closed_and_loader_support_are_separate_mandatory_approvals()
    {
        var bundle = WriteBundle();
        Assert.Throws<ArgumentException>(() =>
            Service().Install(Request(bundle.Path, bundle.Sha, compatible: false)));
        Assert.Throws<ArgumentException>(() =>
            Service().Install(Request(bundle.Path, bundle.Sha, closed: false)));
        _gaming = true;
        Assert.Throws<InvalidOperationException>(() =>
            Service().Install(Request(bundle.Path, bundle.Sha)));
        Assert.Throws<InvalidOperationException>(() => Service().List());
        Assert.Single(Directory.EnumerateFiles(Exports));
        _gaming = false;
        var item = Service().Install(Request(bundle.Path, bundle.Sha));
        Assert.Throws<ArgumentException>(() => Service().Remove(new(item.Id, false, true)));
        Assert.Throws<ArgumentException>(() => Service().Remove(new(item.Id, true, false)));
        _gaming = true;
        Assert.Throws<InvalidOperationException>(() => Service().Remove(new(item.Id, true, true)));
        Assert.True(File.Exists(item.InstalledFile));
    }

    [Theory]
    [InlineData("games")]
    [InlineData("Downloads")]
    [InlineData("mod")]
    [InlineData("bin")]
    [InlineData("Windows")]
    public void Cannot_deploy_into_an_arbitrary_folder(string folderName)
    {
        var bundle = WriteBundle();
        string outside = Path.Combine(_root, folderName);
        Directory.CreateDirectory(outside);
        Assert.Throws<ArgumentException>(() =>
            Service().Install(Request(bundle.Path, bundle.Sha, folder: outside)));
        Assert.Empty(Directory.EnumerateFiles(outside));
    }

    [Fact]
    public void Installing_the_same_export_twice_is_refused_without_touching_existing_mod()
    {
        var bundle = WriteBundle();
        GameModDropInReceiptDto item = Service().Install(Request(bundle.Path, bundle.Sha));
        Assert.Throws<InvalidOperationException>(() =>
            Service().Install(Request(bundle.Path, bundle.Sha)));
        Assert.Equal("installed", Assert.Single(Service().List()).State);
        Assert.Single(Directory.EnumerateFiles(Mods));
        Assert.Equal(File.ReadAllBytes(bundle.Path), File.ReadAllBytes(item.InstalledFile));
    }

    [Fact]
    public void Missing_mod_folder_can_clear_an_orphaned_receipt_after_consent()
    {
        var bundle = WriteBundle();
        GameModDropInReceiptDto item = Service().Install(Request(bundle.Path, bundle.Sha));
        Directory.Delete(Mods, recursive: true);
        Assert.Equal("unsafe", Assert.Single(Service().List()).State);
        Assert.True(Service().Remove(new(item.Id, true, true)));
        Assert.Empty(Service().List());
        Assert.False(Directory.Exists(Mods));
    }

    [Fact]
    public void A_tampered_source_or_executable_ZIP_cannot_be_placed()
    {
        var bundle = WriteBundle();
        Assert.Throws<InvalidOperationException>(() =>
            Service().Install(Request(bundle.Path, new string('0', 64))));
        Assert.Empty(Directory.EnumerateFiles(Mods));
        var malicious = WriteBundle(extraExecutable: true);
        Assert.Throws<InvalidDataException>(() =>
            Service().Install(Request(malicious.Path, malicious.Sha)));
        Assert.Empty(Service().List());
        Assert.Empty(Directory.EnumerateFiles(Mods));
    }

    [Fact]
    public void Traversal_in_archive_manifest_is_refused()
    {
        var bundle = WriteBundle("../outside.png");
        Assert.Throws<ArgumentException>(() =>
            Service().Install(Request(bundle.Path, bundle.Sha)));
        Assert.False(File.Exists(Path.Combine(_root, "outside.png")));
        Assert.Empty(Service().List());
    }
}
