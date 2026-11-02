using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameFolderInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-folder-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Make(string relative, int bytes = 50_000)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    [Fact]
    public void Picks_the_executable_that_matches_the_folder_and_skips_uninstallers_and_redistributables()
    {
        Directory.CreateDirectory(_root);
        Make("Space Ranger/SpaceRanger.exe", 40_000_000);
        Make("Space Ranger/unins000.exe");
        Make("Space Ranger/UnityCrashHandler64.exe");
        Make("Space Ranger/_redist/vcredist_x64.exe");
        Make("Space Ranger/Tools/Editor.exe");
        Make("Space Ranger/setup.exe");
        var result = GameFolderInspector.Inspect(Path.Combine(_root, "Space Ranger"));
        Assert.Null(result.Error);
        Assert.Equal("SpaceRanger.exe", result.Candidates[0].RelativePath);
        Assert.Single(result.Candidates);
        Assert.Contains("matches the folder", result.Candidates[0].Reason);
    }

    [Fact]
    public void Prefers_a_top_level_launcher_over_a_deep_helper_and_ranks_by_size()
    {
        Make("Game/Game.exe", 5_000_000);
        Make("Game/bin/win64/helper.exe", 20_000);
        Make("Game/bin/win64/Game-Win64-Shipping.exe", 90_000_000);
        var result = GameFolderInspector.Inspect(Path.Combine(_root, "Game"));
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal("helper.exe", result.Candidates[^1].RelativePath.Split('/')[^1]);
        Assert.Contains(result.Candidates, c => c.RelativePath == "Game.exe");
    }

    [Fact]
    public void Tiny_files_are_ignored_and_an_empty_folder_reports_a_message_not_an_exception()
    {
        Make("Empty/stub.exe", 100);
        var result = GameFolderInspector.Inspect(Path.Combine(_root, "Empty"));
        Assert.Empty(result.Candidates);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData(@"\\server\share\game")]
    public void Unsafe_or_non_absolute_folders_are_refused(string folder) =>
        Assert.NotNull(GameFolderInspector.Inspect(folder).Error);

    [Fact]
    public void Missing_folder_is_reported()
    {
        Directory.CreateDirectory(_root);
        Assert.NotNull(GameFolderInspector.Inspect(Path.Combine(_root, "nope")).Error);
    }

    [Fact]
    public void Depth_is_bounded()
    {
        Make("G/a/b/c/d/e/Deep.exe", 5_000_000);
        Assert.Empty(GameFolderInspector.Inspect(Path.Combine(_root, "G")).Candidates);
    }

    [Fact]
    public void Entry_budget_truncates_instead_of_scanning_forever()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Big"));
        for (int i = 0; i < GameFolderInspector.MaxEntries + 50; i++)
            File.WriteAllBytes(Path.Combine(_root, "Big", $"f{i}.exe"), new byte[20_000]);
        var result = GameFolderInspector.Inspect(Path.Combine(_root, "Big"));
        Assert.True(result.Truncated);
        Assert.True(result.Candidates.Count <= GameFolderInspector.MaxCandidates);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        Make("C/C.exe", 5_000_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => GameFolderInspector.Inspect(Path.Combine(_root, "C"), cts.Token));
    }

    [Theory]
    [InlineData("Cool_Game_v1.2.3", "Cool Game")]
    [InlineData("My.Game.Name.v2.0", "My Game Name")]
    [InlineData("Dark Quest [Some Tag] (Build 4471)", "Dark Quest")]
    [InlineData("Plain Title", "Plain Title")]
    [InlineData("Game - 1.05", "Game")]
    [InlineData("[tag only]", "[tag only]")]
    public void Titles_are_cleaned_conservatively(string folder, string expected) =>
        Assert.Equal(expected, GameFolderInspector.CleanTitle(folder));

    [Fact]
    public void A_normal_game_folder_beneath_a_linked_parent_is_refused()
    {
        Make("real/subfolder/Example.exe", 5_000_000);
        string alias = Path.Combine(_root, "linked-parent");
        try { Directory.CreateSymbolicLink(alias, Path.Combine(_root, "real")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // OS policy can disallow the synthetic link fixture.
        }
        var result = GameFolderInspector.Inspect(Path.Combine(alias, "subfolder"));
        Assert.NotNull(result.Error);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Junction_folders_are_not_followed()
    {
        Make("Real/Real.exe", 5_000_000);
        string link = Path.Combine(_root, "Link");
        try { Directory.CreateSymbolicLink(link, Path.Combine(_root, "Real")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // symlinks need privilege; skip safely
        Assert.NotNull(GameFolderInspector.Inspect(link).Error);
    }
}
