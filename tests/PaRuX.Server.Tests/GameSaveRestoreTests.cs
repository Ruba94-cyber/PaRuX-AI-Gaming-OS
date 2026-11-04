using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSaveRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-restore-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _backups;
    private bool _gaming;

    public GameSaveRestoreTests()
    {
        _source = Path.Combine(_root, "Game", "Saves");
        _backups = Path.Combine(_root, "Managed");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_backups);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private GameSaveBackupService Make() => new(_backups, () => _gaming, () => CancellationToken.None);

    private async Task<GameSaveBackupDto> BackupOfSampleAsync(GameSaveBackupService service)
    {
        Directory.CreateDirectory(Path.Combine(_source, "slot1"));
        await File.WriteAllTextAsync(Path.Combine(_source, "slot1", "profile.dat"), "original-profile");
        await File.WriteAllTextAsync(Path.Combine(_source, "settings.ini"), "saves=true");
        return await service.CreateAsync(new GameSaveBackupRequest(_source, "Sample"), CancellationToken.None);
    }

    [Fact]
    public async Task Dry_run_lists_actions_and_writes_nothing()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);
        await File.WriteAllTextAsync(Path.Combine(_source, "settings.ini"), "changed");
        File.Delete(Path.Combine(_source, "slot1", "profile.dat"));

        var plan = await service.PlanRestoreAsync(backup.Id, _source, CancellationToken.None);

        Assert.True(plan.Ok, plan.Message);
        Assert.Equal("overwrite", plan.Items.Single(i => i.Name == "settings.ini").Action);
        Assert.Equal("create", plan.Items.Single(i => i.Name == "slot1/profile.dat").Action);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(_source, "settings.ini")));
        Assert.False(File.Exists(Path.Combine(_source, "slot1", "profile.dat")));
        Assert.Single(service.List()); // no pre-restore backup from a dry run
    }

    [Fact]
    public async Task Restore_needs_confirmation()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);
        await File.WriteAllTextAsync(Path.Combine(_source, "settings.ini"), "changed");

        var result = await service.RestoreAsync(backup.Id, new GameSaveRestoreRequest(_source), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("changed", await File.ReadAllTextAsync(Path.Combine(_source, "settings.ini")));
    }

    [Fact]
    public async Task Restore_makes_pre_restore_backup_then_restores_and_verifies()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);
        await File.WriteAllTextAsync(Path.Combine(_source, "settings.ini"), "progress-after-backup");
        await File.WriteAllTextAsync(Path.Combine(_source, "extra.sav"), "keep-me");

        var result = await service.RestoreAsync(backup.Id, new GameSaveRestoreRequest(_source, true), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.FilesWritten);
        Assert.Equal("saves=true", await File.ReadAllTextAsync(Path.Combine(_source, "settings.ini")));
        Assert.Equal("keep-me", await File.ReadAllTextAsync(Path.Combine(_source, "extra.sav"))); // never deletes extras
        Assert.NotNull(result.PreRestoreBackupId);
        Assert.Equal(2, service.List().Count);
        Assert.True((await service.VerifyAsync(result.PreRestoreBackupId!, CancellationToken.None)).Valid);
        Assert.Empty(Directory.GetDirectories(_source, ".parux-restore-*")); // staging cleaned up

        // The pre-restore backup really holds the overwritten state.
        string other = Path.Combine(_root, "Elsewhere");
        Directory.CreateDirectory(other);
        var restoreOld = await service.RestoreAsync(result.PreRestoreBackupId!, new GameSaveRestoreRequest(other, true), CancellationToken.None);
        Assert.True(restoreOld.Success, restoreOld.Message);
        Assert.Equal("progress-after-backup", await File.ReadAllTextAsync(Path.Combine(other, "settings.ini")));
    }

    [Fact]
    public async Task Restore_into_empty_target_needs_no_pre_restore_backup()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);
        string empty = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(empty);

        var result = await service.RestoreAsync(backup.Id, new GameSaveRestoreRequest(empty, true), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.FilesWritten);
        Assert.Null(result.PreRestoreBackupId);
        Assert.Equal("original-profile", await File.ReadAllTextAsync(Path.Combine(empty, "slot1", "profile.dat")));
    }

    [Fact]
    public async Task Tampered_archive_is_refused_and_target_untouched()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);
        await File.WriteAllTextAsync(Path.Combine(_source, "settings.ini"), "newer");
        string zip = Path.Combine(_backups, "save-" + backup.Id + ".zip");
        byte[] bytes = await File.ReadAllBytesAsync(zip);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(zip, bytes);

        var plan = await service.PlanRestoreAsync(backup.Id, _source, CancellationToken.None);
        var result = await service.RestoreAsync(backup.Id, new GameSaveRestoreRequest(_source, true), CancellationToken.None);

        Assert.False(plan.Ok);
        Assert.False(result.Success);
        Assert.Equal("newer", await File.ReadAllTextAsync(Path.Combine(_source, "settings.ini")));
        Assert.Single(service.List()); // no pre-restore backup either
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData(@"C:\evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("back\\slash.txt")]
    [InlineData("stream.txt:hidden")]
    [InlineData("trailing.")]
    [InlineData("trailing /x")]
    [InlineData("")]
    [InlineData("a//b")]
    public void Names_that_could_escape_are_refused(string name)
    {
        string target = Path.Combine(_root, "T");
        Assert.False(GameSaveBackupService.TryResolveInside(target, name, out _));
    }

    [Fact]
    public void Plain_nested_name_resolves_inside_target()
    {
        string target = Path.Combine(_root, "T");
        Assert.True(GameSaveBackupService.TryResolveInside(target, "slot1/profile.dat", out string full));
        Assert.StartsWith(Path.GetFullPath(target), full);
    }

    [Fact]
    public async Task Hand_crafted_manifest_with_drive_relative_name_is_refused()
    {
        // ReadManifest only rejects "..", rooted-with-slash and empty parts; the restore layer must catch "C:x" itself.
        const string name = "C:evil.txt";
        byte[] content = "payload"u8.ToArray();
        string id = Guid.NewGuid().ToString("N");
        string zipPath = Path.Combine(_backups, "save-" + id + ".zip");
        using (var fs = new FileStream(zipPath, FileMode.CreateNew))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(name);
            using var s = entry.Open();
            s.Write(content);
        }
        string zipHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipPath))).ToLowerInvariant();
        var manifest = new
        {
            Version = 1,
            Backup = new GameSaveBackupDto(id, "crafted", 1, content.Length, DateTime.UtcNow, zipHash),
            Items = new[] { new { Name = name, Bytes = (long)content.Length, Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() } }
        };
        await File.WriteAllTextAsync(Path.Combine(_backups, "save-" + id + ".json"), JsonSerializer.Serialize(manifest));
        string target = Path.Combine(_root, "Target");
        Directory.CreateDirectory(target);
        var service = Make();

        var plan = await service.PlanRestoreAsync(id, target, CancellationToken.None);
        var result = await service.RestoreAsync(id, new GameSaveRestoreRequest(target, true), CancellationToken.None);

        Assert.False(plan.Ok);
        Assert.False(result.Success);
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "evil.txt")));
    }

    [Fact]
    public async Task Target_inside_managed_backup_folder_or_relative_is_refused()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);

        Assert.False((await service.PlanRestoreAsync(backup.Id, _backups, CancellationToken.None)).Ok);
        Assert.False((await service.PlanRestoreAsync(backup.Id, Path.Combine(_backups, "x"), CancellationToken.None)).Ok);
        Assert.False((await service.PlanRestoreAsync(backup.Id, _root, CancellationToken.None)).Ok); // contains the backup root
        Assert.False((await service.PlanRestoreAsync(backup.Id, "relative\\folder", CancellationToken.None)).Ok);
        Assert.False((await service.PlanRestoreAsync("not-an-id", _source, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Gaming_blocks_plan_and_restore()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);
        await File.WriteAllTextAsync(Path.Combine(_source, "settings.ini"), "newer");
        _gaming = true;

        Assert.False((await service.PlanRestoreAsync(backup.Id, _source, CancellationToken.None)).Ok);
        Assert.False((await service.RestoreAsync(backup.Id, new GameSaveRestoreRequest(_source, true), CancellationToken.None)).Success);
        Assert.Equal("newer", await File.ReadAllTextAsync(Path.Combine(_source, "settings.ini")));
    }

    [Fact]
    public async Task Unchanged_target_reports_nothing_to_do()
    {
        var service = Make();
        var backup = await BackupOfSampleAsync(service);

        var result = await service.RestoreAsync(backup.Id, new GameSaveRestoreRequest(_source, true), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.FilesWritten);
        Assert.Null(result.PreRestoreBackupId);
        Assert.Single(service.List());
    }
}
