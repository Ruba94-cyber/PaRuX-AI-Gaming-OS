using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSaveHookPresenterTests
{
    private static GameSaveRestorePlanDto Plan(params (string Name, string Action)[] items) =>
        new("id", true, "ok", items.Select(i => new GameSaveRestoreItemDto(i.Name, 10, i.Action)).ToList());

    [Fact]
    public void Confirmation_states_the_counts_and_the_safety_guarantees()
    {
        var plan = Plan(("a.sav", "create"), ("b.sav", "overwrite"), ("c.sav", "overwrite"), ("d.sav", "unchanged"));

        string text = GameSaveHookPresenter.ConfirmationText(plan, false);

        Assert.Contains("1 new files, 2 replaced, 1 already identical", text);
        Assert.Contains("automatically saves a copy", text);
        Assert.Contains("never deleted", text);
        Assert.Contains("SHA-256", text);
        Assert.Contains("+ a.sav", text);
        Assert.Contains("~ b.sav", text);
        Assert.DoesNotContain("d.sav", text);   // unchanged files are not listed
    }

    [Fact]
    public void Confirmation_preview_is_bounded()
    {
        var plan = Plan(Enumerable.Range(0, 30).Select(i => ($"f{i}.sav", "create")).ToArray());

        string text = GameSaveHookPresenter.ConfirmationText(plan, false);

        Assert.Equal(GameSaveHookPresenter.PreviewNames, text.Split('\n').Count(l => l.StartsWith("+ ", StringComparison.Ordinal)));
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Restore_outcome_never_claims_success_for_a_failed_restore()
    {
        var failed = new GameSaveRestoreResultDto("id", false, "hash mismatch", 0, null);
        var ok = new GameSaveRestoreResultDto("id", true, "done", 3, "pre-1");
        var okEmpty = new GameSaveRestoreResultDto("id", true, "done", 3, null);

        Assert.StartsWith("Restore failed: hash mismatch", GameSaveHookPresenter.RestoreOutcome(failed, false));
        Assert.DoesNotContain("Restored", GameSaveHookPresenter.RestoreOutcome(failed, false));
        Assert.Contains("Restored 3 files and verified them", GameSaveHookPresenter.RestoreOutcome(ok, false));
        Assert.Contains("copy of the previous files", GameSaveHookPresenter.RestoreOutcome(ok, false));
        Assert.Contains("no pre-restore copy was needed", GameSaveHookPresenter.RestoreOutcome(okEmpty, false));
        Assert.Contains("Ripristinati 3 file", GameSaveHookPresenter.RestoreOutcome(ok, true));
    }

    [Fact]
    public void Per_game_backups_are_matched_by_label()
    {
        var mine = new GameSaveBackupDto("1", "My Game", 2, 20, DateTime.UtcNow, "x");
        var other = new GameSaveBackupDto("2", "Other", 2, 20, DateTime.UtcNow, "x");

        Assert.True(GameSaveHookPresenter.BelongsToGame(mine, "my game"));
        Assert.False(GameSaveHookPresenter.BelongsToGame(other, "My Game"));
        Assert.Equal("Game", GameSaveHookPresenter.BackupLabel("  \u0001 "));
        Assert.Equal(100, GameSaveHookPresenter.BackupLabel(new string('x', 300)).Length);
    }

    [Fact]
    public void Hook_form_empty_path_means_unset_without_an_error()
    {
        Assert.Equal((null, null), GameSaveHookPresenter.ParseHookForm("  ", "x", "99", false));
    }

    [Theory]
    [InlineData("C:\\tools\\run.bat")]
    [InlineData("C:\\tools\\run.ps1")]
    [InlineData("C:\\tools\\run.lnk")]
    [InlineData("C:\\tools\\run")]
    public void Hook_form_refuses_anything_but_an_exe(string path)
    {
        var (spec, error) = GameSaveHookPresenter.ParseHookForm(path, "", "15", false);
        Assert.Null(spec);
        Assert.Contains(".exe", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("abc")]
    [InlineData("-5")]
    public void Hook_form_refuses_a_timeout_outside_1_to_60(string timeout)
    {
        var (spec, error) = GameSaveHookPresenter.ParseHookForm("C:\\tools\\run.exe", "", timeout, false);
        Assert.Null(spec);
        Assert.NotNull(error);
    }

    [Fact]
    public void Hook_form_accepts_a_valid_exe_and_trims_quotes()
    {
        var (spec, error) = GameSaveHookPresenter.ParseHookForm("\"C:\\tools\\run.exe\"", "--quiet", "30", false);
        Assert.Null(error);
        Assert.Equal(new GameHookSpec("C:\\tools\\run.exe", "--quiet", 30), spec);
        Assert.Equal(15, GameSaveHookPresenter.ParseHookForm("C:\\tools\\run.exe", "", "", false).Spec!.TimeoutSeconds);
    }

    [Fact]
    public void Hook_form_refuses_control_characters_and_long_arguments()
    {
        Assert.NotNull(GameSaveHookPresenter.ParseHookForm("C:\\a.exe", "a\nb", "5", false).Error);
        Assert.NotNull(GameSaveHookPresenter.ParseHookForm("C:\\a.exe", new string('x', 1025), "5", false).Error);
    }

    [Fact]
    public void Hook_confirmation_names_the_exact_program_and_the_limits()
    {
        var text = GameSaveHookPresenter.HookConfirmationText(
            new GameHooksDto(new GameHookSpec("C:\\t\\pre.exe", "-a", 10), new GameHookSpec("C:\\t\\post.exe", "", 20)), false);

        Assert.Contains("C:\\t\\pre.exe -a (10 s)", text);
        Assert.Contains("C:\\t\\post.exe (20 s)", text);
        Assert.Contains("never as administrator", text);
        Assert.Contains("never starts while Gaming is active", text);
    }
}

public sealed class IgdbParsingTests
{
    private const string Games = """
        [
          {"id": 7346, "name": "Synthetic Racer", "summary": "A fictional racing game.", "first_release_date": 1500000000,
           "genres": [{"id": 1, "name": "Racing"}, {"id": 2, "name": "Arcade"}],
           "cover": {"id": 5, "image_id": "co1abc"},
           "involved_companies": [{"developer": false, "company": {"name": "Publisher Co"}}, {"developer": true, "company": {"name": "Dev Studio"}}]},
          {"id": 9, "name": "Totally Different Title", "cover": {"image_id": "co2zzz"}}
        ]
        """;

    [Theory]
    [InlineData("abcdefghij1234:secretvalue98765", true)]
    [InlineData("abcdefghij1234\nsecretvalue98765", true)]
    [InlineData("abcdefghij1234", false)]
    [InlineData("short:short", false)]
    [InlineData("abcdefghij1234:abcdefghij1234", false)]
    [InlineData("abc defghij1234:secretvalue98765", false)]
    [InlineData("https://x.example:secretvalue98765", false)]
    [InlineData("", false)]
    public void Credentials_need_two_distinct_alphanumeric_parts(string input, bool valid) =>
        Assert.Equal(valid, GameMetadataParsing.ParseIgdbCredentials(input) is not null);

    [Fact]
    public void Token_reply_needs_a_token_and_a_positive_lifetime()
    {
        Assert.Equal(("abcdefghij1234567890", 5000), GameMetadataParsing.ParseIgdbToken("""{"access_token":"abcdefghij1234567890","expires_in":5000,"token_type":"bearer"}"""));
        Assert.Null(GameMetadataParsing.ParseIgdbToken("""{"access_token":"abcdefghij1234567890"}"""));
        Assert.Null(GameMetadataParsing.ParseIgdbToken("""{"access_token":"bad token!","expires_in":10}"""));
        Assert.Null(GameMetadataParsing.ParseIgdbToken("not json"));
        Assert.Null(GameMetadataParsing.ParseIgdbToken(null));
    }

    [Fact]
    public void Query_cannot_be_broken_out_of_by_the_game_name()
    {
        string q = GameMetadataParsing.BuildIgdbQuery("Evil\" ; delete games;\\ \n name");

        Assert.StartsWith("search \"", q);
        Assert.DoesNotContain("\\", q);
        Assert.DoesNotContain("\n", q);
        Assert.Equal(3, q.Count(c => c == ';')); // search clause, fields clause, limit clause only
        Assert.Equal(2, q.Count(c => c == '"'));
    }

    [Fact]
    public void Best_match_gives_cover_genres_developer_and_date()
    {
        var info = GameMetadataParsing.ParseIgdbBest("synthracer", "Synthetic Racer", Games)!;

        Assert.Equal(GameMetadataParsing.SourceIgdb, info.Source);
        Assert.Equal("Synthetic Racer", info.Name);
        Assert.Equal(new[] { "Racing", "Arcade" }, info.Genres);
        Assert.Equal("Dev Studio", info.Developer);
        Assert.Equal("2017-07-14", info.Released);
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_big/co1abc.jpg", info.CoverUrl);
        Assert.Equal("https://www.igdb.com/games/7346", info.PageUrl);
        Assert.True(GameMetadataParsing.IsAllowedImageUrl(info.CoverUrl));
    }

    [Fact]
    public void A_weak_match_is_refused_rather_than_putting_a_wrong_cover_on_a_game()
    {
        Assert.Null(GameMetadataParsing.ParseIgdbBest("k", "Completely Unrelated Words", Games));
        Assert.Null(GameMetadataParsing.ParseIgdbBest("k", "Synthetic Racer", "[]"));
        Assert.Null(GameMetadataParsing.ParseIgdbBest("k", "Synthetic Racer", """{"message":"error"}"""));
        Assert.Null(GameMetadataParsing.ParseIgdbBest("k", "Synthetic Racer", "garbage"));
    }

    [Fact]
    public void Hostile_image_ids_and_hosts_are_not_followed()
    {
        string hostile = """[{"id":1,"name":"Synthetic Racer","cover":{"image_id":"../../evil?x=1"}}]""";
        Assert.Null(GameMetadataParsing.ParseIgdbBest("k", "Synthetic Racer", hostile)!.CoverUrl);

        Assert.True(GameMetadataParsing.IsIgdbImageUrl("https://images.igdb.com/igdb/image/upload/t_cover_big/x.jpg"));
        Assert.False(GameMetadataParsing.IsIgdbImageUrl("http://images.igdb.com/x.jpg"));
        Assert.False(GameMetadataParsing.IsIgdbImageUrl("https://images.igdb.com.evil.example/x.jpg"));
        Assert.False(GameMetadataParsing.IsIgdbImageUrl("https://user@images.igdb.com/x.jpg"));
        Assert.False(GameMetadataParsing.IsAllowedImageUrl("https://api.igdb.com/x.jpg"));
    }
}
