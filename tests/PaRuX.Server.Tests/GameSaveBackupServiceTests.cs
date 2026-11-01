using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSaveBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-save-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _backups;
    private bool _gaming;

    public GameSaveBackupServiceTests()
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

    private GameSaveBackupService Make() =>
        new(_backups, () => _gaming, () => CancellationToken.None);

    [Fact]
    public async Task Local_save_snapshot_preserves_originals_and_verifies_every_file()
    {
        Directory.CreateDirectory(Path.Combine(_source, "slot1"));
        byte[] data = Enumerable.Range(0, 2048).Select(i => (byte)(i % 256)).ToArray();
        string slot = Path.Combine(_source, "slot1", "profile.dat");
        string cfg = Path.Combine(_source, "settings.ini");
        await File.WriteAllBytesAsync(slot, data);
        await File.WriteAllTextAsync(cfg, "saves=true");
        var service = Make();

        var created = await service.CreateAsync(new GameSaveBackupRequest(_source, "Personal save"), CancellationToken.None);
        Assert.Equal(2, created.Files);
        Assert.True(created.Bytes >= data.Length);
        Assert.Equal(64, created.ArchiveSha256.Length);
        Assert.Single(service.List());
        var verification = await service.VerifyAsync(created.Id, CancellationToken.None);
        Assert.True(verification.Valid, verification.Message);
        Assert.Equal(2, verification.VerifiedFiles);
        Assert.Equal(data, await File.ReadAllBytesAsync(slot));
        Assert.Equal("saves=true", await File.ReadAllTextAsync(cfg));
        Assert.DoesNotContain(_source, File.ReadAllText(Path.Combine(_backups, "save-" + created.Id + ".json")));
    }

    [Fact]
    public async Task Corrupt_archive_is_detected_without_restoring_or_touching_the_game()
    {
        await File.WriteAllTextAsync(Path.Combine(_source, "progress.sav"), "level=8");
        var service = Make();
        var item = await service.CreateAsync(new GameSaveBackupRequest(_source, "Slot"), CancellationToken.None);
        var archive = Path.Combine(_backups, "save-" + item.Id + ".zip");
        await File.AppendAllTextAsync(archive, "tampered");
        var result = await service.VerifyAsync(item.Id, CancellationToken.None);
        Assert.False(result.Valid);
        Assert.Equal("level=8", await File.ReadAllTextAsync(Path.Combine(_source, "progress.sav")));
    }

    [Fact]
    public async Task Gaming_blocks_creation_and_verification()
    {
        await File.WriteAllTextAsync(Path.Combine(_source, "slot.sav"), "data");
        var service = Make();
        var item = await service.CreateAsync(new GameSaveBackupRequest(_source, "Slot"), CancellationToken.None);
        _gaming = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new GameSaveBackupRequest(_source, "Another"), CancellationToken.None));
        var result = await service.VerifyAsync(item.Id, CancellationToken.None);
        Assert.False(result.Valid);
        Assert.Single(service.List());
    }

    [Fact]
    public async Task Invalid_label_and_deep_nested_folders_fail_without_published_backup()
    {
        var service = Make();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CreateAsync(new GameSaveBackupRequest(_source, "bad\nlabel"), CancellationToken.None));
        string current = _source;
        for (int i = 0; i < 10; i++)
        {
            current = Path.Combine(current, "nested" + i);
            Directory.CreateDirectory(current);
        }
        await File.WriteAllTextAsync(Path.Combine(current, "save.dat"), "data");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CreateAsync(new GameSaveBackupRequest(_source, "Too deep"), CancellationToken.None));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task Empty_folder_creates_no_archive_and_is_left_unchanged()
    {
        var service = Make();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CreateAsync(new GameSaveBackupRequest(_source, "Empty"), CancellationToken.None));
        Assert.Empty(service.List());
    }
}
