using System;
using System.IO;
using System.Linq;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure synthetic local files; no game launches, P2P or kernel process suspension.</summary>
public sealed class TemporaryGameModServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-temp-mod-" + Guid.NewGuid().ToString("N"));
    private readonly string _gameId = "game-" + new string('a', 32);
    private readonly DateTime _started = new(2026, 11, 05, 10, 0, 0, DateTimeKind.Utc);
    private ManualGameLibrary _library = null!;
    private bool _paused = true;
    private string _entryId = "";
    private string ManagedRoot => Path.Combine(_root, "portable-games");
    private string Config => Path.Combine(ManagedRoot, _gameId, "config", "settings.ini");
    private string Backups => Path.Combine(_root, "temp-mod-backups");

    public TemporaryGameModServiceTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        File.WriteAllText(Path.Combine(ManagedRoot, _gameId, "Demo.exe"), "inert bytes");
        File.WriteAllText(Config, "[video]\nbrightness=1\n");
        _library = new ManualGameLibrary(Path.Combine(_root, "games.json"));
        _entryId = _library.Add(new AddManualGameRequest("Synthetic Game",
            Path.Combine(ManagedRoot, _gameId, "Demo.exe"))).Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { }
    }

    private TemporaryGameModService Service() =>
        new(_library, ManagedRoot, Backups, (pid, started, exe) =>
            _paused && pid == 999999 && QuickResumePolicy.SameProcess(_started, started)
                && exe.EndsWith("Demo.exe", StringComparison.OrdinalIgnoreCase));

    private GameModProposalRequest Proposed(string path = "config/settings.ini", bool offline = true) =>
        new(_entryId, 999999, _started, path, "[video]\nbrightness=2\n", offline);

    [Fact]
    public void Preview_only_reads_then_explicit_apply_and_restore_preserve_the_original()
    {
        var service = Service();
        string baseline = File.ReadAllText(Config);
        GameModPreviewDto preview = service.Preview(Proposed());
        Assert.Equal(baseline, preview.OriginalText);
        Assert.Equal("[video]\nbrightness=2\n", preview.ProposedText);
        Assert.Equal(baseline, File.ReadAllText(Config));
        var pending = Assert.Single(service.ListPreviews());
        Assert.Equal(preview.PreviewId, pending.PreviewId);
        Assert.Equal(baseline, pending.OriginalText);
        Assert.Equal(preview.ProposedText, pending.ProposedText);

        Assert.Throws<ArgumentException>(() =>
            service.Apply(new GameModApplyRequest(preview.PreviewId, Consent: false)));
        Assert.Equal(baseline, File.ReadAllText(Config));

        GameTemporaryModDto applied = service.Apply(new(preview.PreviewId, true));
        Assert.Equal("[video]\nbrightness=2\n", File.ReadAllText(Config));
        Assert.Single(service.List());
        Assert.Empty(service.ListPreviews()); // Preview token was single-use.
        Assert.Equal(applied.Id, Assert.Single(Service().List()).Id);

        Assert.True(Service().Restore(new GameModRestoreRequest(applied.Id, true)));
        Assert.Equal(baseline, File.ReadAllText(Config));
        Assert.Empty(service.List());
    }

    [Fact]
    public void No_offline_confirmation_or_no_paused_game_never_grants_a_preview()
    {
        var service = Service();
        Assert.Throws<ArgumentException>(() => service.Preview(Proposed(offline: false)));
        _paused = false;
        Assert.Throws<InvalidOperationException>(() => service.Preview(Proposed()));
        Assert.Equal("[video]\nbrightness=1\n", File.ReadAllText(Config));
    }

    [Theory]
    [InlineData("../secret.ini")]
    [InlineData("/tmp/outside.json")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("config/../../win.ini")]
    [InlineData("config/../settings.ini")]
    [InlineData("Game.exe")]
    [InlineData("config/autorun.bat")]
    [InlineData("config/.parux-lock.ini")]
    public void Unsafe_or_executable_paths_are_refused(string path)
    {
        Assert.False(TemporaryGameModService.SafeRelativeConfig(path));
    }

    [Fact]
    public void A_changed_file_between_preview_and_apply_is_never_overwritten()
    {
        var service = Service();
        var preview = service.Preview(Proposed());
        File.WriteAllText(Config, "[video]\nbrightness=999\n");
        Assert.Throws<InvalidOperationException>(() => service.Apply(new(preview.PreviewId, true)));
        Assert.Equal("[video]\nbrightness=999\n", File.ReadAllText(Config));
        Assert.Empty(service.List());
    }

    [Fact]
    public void An_external_change_after_apply_refuses_restore_and_preserves_recovery_record()
    {
        var service = Service();
        var preview = service.Preview(Proposed());
        var applied = service.Apply(new(preview.PreviewId, true));
        File.WriteAllText(Config, "[video]\nbrightness=3\n");
        Assert.Throws<InvalidOperationException>(() => service.Restore(new(applied.Id, true)));
        Assert.Equal("[video]\nbrightness=3\n", File.ReadAllText(Config));
        Assert.Single(service.List());
    }

    [Fact]
    public void Duplicate_active_mod_on_the_same_file_is_blocked()
    {
        var service = Service();
        var applied = service.Apply(new(service.Preview(Proposed()).PreviewId, true));
        Assert.Throws<InvalidOperationException>(() => service.Preview(Proposed()));
        Assert.True(service.Restore(new(applied.Id, true)));
    }

    [Fact]
    public void An_invalid_JSON_proposal_is_rejected_before_writing()
    {
        string json = Path.Combine(Path.GetDirectoryName(Config)!, "options.json");
        File.WriteAllText(json, "{\"scale\":1}");
        var request = new GameModProposalRequest(_entryId, 999999, _started, "config/options.json",
            "{malformed", OfflineSinglePlayerConfirmed: true);
        Assert.Throws<ArgumentException>(() => Service().Preview(request));
        Assert.Equal("{\"scale\":1}", File.ReadAllText(json));
    }

    [Fact]
    public void Unknown_library_game_or_foreign_executable_cannot_be_modified()
    {
        var request = Proposed() with { ManualGameId = "unknown" };
        Assert.Throws<InvalidOperationException>(() => Service().Preview(request));
        string unrelated = Path.Combine(_root, "other.ini");
        File.WriteAllText(unrelated, "keep");
        Assert.False(TemporaryGameModService.SafeRelativeConfig("../other.ini"));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }
}
