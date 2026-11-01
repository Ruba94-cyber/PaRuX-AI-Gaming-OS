using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManualEntryKindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-kind-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;
    private readonly string _file;

    public ManualEntryKindTests()
    {
        Directory.CreateDirectory(_dir);
        _exe = Path.Combine(_dir, "tool.exe");
        File.WriteAllBytes(_exe, new byte[] { 0x4D, 0x5A });
        _file = Path.Combine(_dir, "library.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Old_library_files_without_new_fields_load_as_plain_games()
    {
        File.WriteAllText(_file,
            "{\"Version\":1,\"Games\":[{\"Id\":\"a1\",\"Name\":\"Old\",\"ExecutablePath\":\"" + _exe.Replace("\\", "\\\\") + "\"}]}");
        var game = Assert.Single(new ManualGameLibrary(_file).List());
        Assert.Equal(ManualEntryKinds.Game, game.Kind);
        Assert.Equal("", game.Arguments);
        Assert.Equal("", game.WorkingDirectory);
    }

    [Fact]
    public void Application_with_arguments_and_working_directory_round_trips()
    {
        var lib = new ManualGameLibrary(_file);
        var added = lib.Add(new AddManualGameRequest("Emulator", _exe, "--fullscreen \"rom one.bin\"", _dir, "Application"));
        Assert.Equal(ManualEntryKinds.Application, added.Kind);
        var reloaded = Assert.Single(new ManualGameLibrary(_file).List());
        Assert.Equal("--fullscreen \"rom one.bin\"", reloaded.Arguments);
        Assert.Equal(Path.GetFullPath(_dir), reloaded.WorkingDirectory);
    }

    [Fact]
    public void Same_program_with_different_arguments_is_allowed_but_exact_duplicates_are_not()
    {
        var lib = new ManualGameLibrary(_file);
        lib.Add(new AddManualGameRequest("A", _exe, "rom1"));
        lib.Add(new AddManualGameRequest("B", _exe, "rom2"));
        Assert.Throws<InvalidOperationException>(() => lib.Add(new AddManualGameRequest("C", _exe, "rom1")));
        Assert.Equal(2, lib.List().Count);
    }

    [Theory]
    [InlineData("tool", "", "")]                 // unknown kind
    [InlineData("game", "bad\r\nargs", "")]      // control characters
    [InlineData("game", "ok", "relative/dir")]  // not absolute
    [InlineData("game", "ok", @"\\server\share")] // UNC
    public void Invalid_kind_arguments_or_working_directory_are_rejected(string kind, string args, string workDir) =>
        Assert.Throws<ArgumentException>(() => new ManualGameLibrary(_file).Add(new AddManualGameRequest("X", _exe, args, workDir, kind)));

    [Fact]
    public void Oversized_arguments_and_missing_working_directory_are_rejected()
    {
        var lib = new ManualGameLibrary(_file);
        Assert.Throws<ArgumentException>(() => lib.Add(new AddManualGameRequest("X", _exe, new string('a', 1025))));
        Assert.Throws<ArgumentException>(() => lib.Add(new AddManualGameRequest("X", _exe, "", Path.Combine(_dir, "missing"))));
    }

    [Fact]
    public void Launch_options_are_rechecked_at_launch_time()
    {
        var game = new ManualGameDto("id", "n", _exe, "arg", Path.Combine(_dir, "gone"));
        Assert.NotNull(ManualGameSessionService.ValidateLaunchOptions(game));
        Assert.Null(ManualGameSessionService.ValidateLaunchOptions(game with { WorkingDirectory = _dir }));
        Assert.NotNull(ManualGameSessionService.ValidateLaunchOptions(game with { WorkingDirectory = _dir, Arguments = "x\0y" }));
    }

    [Fact]
    public void Dto_json_round_trip_keeps_kind_and_arguments()
    {
        var dto = new ManualGameDto("i", "n", @"C:\x.exe", "a b", @"C:\", ManualEntryKinds.Application);
        Assert.Equal(dto, JsonSerializer.Deserialize<ManualGameDto>(JsonSerializer.Serialize(dto)));
    }
}
