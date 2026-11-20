using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSaveBackupSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-save-sync-" + Guid.NewGuid().ToString("N"));
    private bool _gaming;

    public GameSaveBackupSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Each "PC" has its own managed backup directory (and its own config file beside it).
    private GameSaveBackupService Pc(string name)
    {
        string home = Path.Combine(_root, name);
        Directory.CreateDirectory(home);
        return new GameSaveBackupService(Path.Combine(home, "backups"), () => _gaming, () => CancellationToken.None);
    }

    private async Task<string> Backup(GameSaveBackupService pc, string label, string content)
    {
        string source = Path.Combine(_root, "saves-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "slot.dat"), content);
        return (await pc.CreateAsync(new GameSaveBackupRequest(source, label), CancellationToken.None)).Id;
    }

    private string Mirror()
    {
        string mirror = Path.Combine(_root, "cloud-folder");
        Directory.CreateDirectory(mirror);
        return mirror;
    }

    [Fact]
    public async Task A_backup_made_on_one_pc_reaches_another_pc_through_the_mirror_folder_and_verifies_there()
    {
        var a = Pc("A"); var b = Pc("B");
        string id = await Backup(a, "Slot 1", "progress=42");
        string mirror = Mirror();
        a.SetSyncFolder(mirror); b.SetSyncFolder(mirror);

        GameSaveSyncResultDto pushed = await a.SyncAsync(CancellationToken.None);
        Assert.Equal(1, pushed.Pushed);
        Assert.Empty(pushed.Problems);
        Assert.True(File.Exists(Path.Combine(mirror, "PaRuX-save-backups", "save-" + id + ".zip")));

        GameSaveSyncResultDto pulled = await b.SyncAsync(CancellationToken.None);
        Assert.Equal(1, pulled.Pulled);
        GameSaveBackupDto arrived = Assert.Single(b.List());
        Assert.Equal(id, arrived.Id);
        Assert.True((await b.VerifyAsync(id, CancellationToken.None)).Valid);
    }

    [Fact]
    public async Task A_second_sync_changes_nothing_and_never_overwrites()
    {
        var a = Pc("A");
        await Backup(a, "One", "a");
        a.SetSyncFolder(Mirror());
        await a.SyncAsync(CancellationToken.None);
        GameSaveSyncResultDto again = await a.SyncAsync(CancellationToken.None);
        Assert.Equal(0, again.Pushed);
        Assert.Equal(0, again.Pulled);
        Assert.Equal(1, again.AlreadyThere);
    }

    [Fact]
    public async Task A_mirror_archive_that_was_tampered_with_is_refused_and_leaves_nothing_behind()
    {
        var a = Pc("A"); var b = Pc("B");
        string id = await Backup(a, "Slot", "good");
        string mirror = Mirror();
        a.SetSyncFolder(mirror); b.SetSyncFolder(mirror);
        await a.SyncAsync(CancellationToken.None);

        string zip = Path.Combine(mirror, "PaRuX-save-backups", "save-" + id + ".zip");
        byte[] bytes = await File.ReadAllBytesAsync(zip);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(zip, bytes);

        GameSaveSyncResultDto result = await b.SyncAsync(CancellationToken.None);
        Assert.Equal(0, result.Pulled);
        Assert.NotEmpty(result.Problems);
        Assert.Empty(b.List());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "B", "backups"), "save-*"));
    }

    [Fact]
    public async Task Sync_needs_a_folder_and_refuses_the_managed_directory_and_Gaming()
    {
        var a = Pc("A");
        await Assert.ThrowsAsync<InvalidDataException>(() => a.SyncAsync(CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => a.SetSyncFolder(Path.Combine(_root, "does-not-exist")));
        Directory.CreateDirectory(Path.Combine(_root, "A", "backups"));
        Assert.Throws<InvalidDataException>(() => a.SetSyncFolder(Path.Combine(_root, "A", "backups")));
        Assert.Throws<InvalidDataException>(() => a.SetSyncFolder("relative\\path"));

        a.SetSyncFolder(Mirror());
        _gaming = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SyncAsync(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => a.SetSyncFolder(null));
    }

    [Fact]
    public void The_folder_choice_is_remembered_and_can_be_cleared()
    {
        var a = Pc("A");
        string mirror = Mirror();
        Assert.False(a.SyncStatus().Configured);
        a.SetSyncFolder(mirror);
        Assert.Equal(Path.GetFullPath(mirror), Pc("A").SyncStatus().Folder);
        Assert.False(a.SetSyncFolder("").Configured);
        Assert.False(Pc("A").SyncStatus().Configured);
    }
}
