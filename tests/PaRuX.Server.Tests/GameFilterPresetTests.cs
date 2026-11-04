using System.Text.Json;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameFilterPresetTests
{
    private static GameFilterPreset P(string name, Action<GameFilterPreset>? edit = null)
    {
        var p = new GameFilterPreset { Name = name };
        edit?.Invoke(p);
        return p;
    }

    [Fact]
    public void Valid_preset_round_trips_all_fields()
    {
        var list = GameFilterPresets.Upsert(null, P("Couch", p =>
        {
            p.Text = "racing"; p.Sort = GameTitleSorts.PlayTime; p.Source = GameInstallationSources.Steam;
            p.Platform = "PC"; p.Installed = "installed"; p.FavoritesOnly = true; p.Tag = "co-op";
            p.Collection = "Weekend"; p.ShowHidden = true;
        }))!;

        var back = JsonSerializer.Deserialize<List<GameFilterPreset>>(JsonSerializer.Serialize(list))!;

        var one = Assert.Single(back);
        Assert.Equal("Couch", one.Name);
        Assert.Equal(GameTitleSorts.PlayTime, one.Sort);
        Assert.Equal(GameInstallationSources.Steam, one.Source);
        Assert.Equal("installed", one.Installed);
        Assert.True(one.FavoritesOnly);
        Assert.True(one.ShowHidden);
        Assert.Equal("Weekend", one.Collection);
    }

    [Fact]
    public void Same_name_replaces_case_insensitively_and_does_not_mutate_the_input()
    {
        var first = GameFilterPresets.Upsert(null, P("Couch", p => p.Tag = "a"))!;
        var second = GameFilterPresets.Upsert(first, P("couch", p => p.Tag = "b"))!;

        Assert.Equal("a", Assert.Single(first).Tag);
        Assert.Equal("b", Assert.Single(second).Tag);
    }

    [Fact]
    public void Empty_or_control_only_names_are_rejected()
    {
        Assert.Null(GameFilterPresets.Upsert(null, P("   ")));
        Assert.Null(GameFilterPresets.Upsert(null, P("\n\t")));
        Assert.Null(GameFilterPresets.Upsert(null, null));
    }

    [Fact]
    public void Cap_of_twelve_blocks_new_names_but_still_allows_replacing()
    {
        List<GameFilterPreset> list = new();
        for (int i = 0; i < GameFilterPresets.MaxPresets; i++) list = GameFilterPresets.Upsert(list, P("p" + i))!;

        Assert.Null(GameFilterPresets.Upsert(list, P("one-too-many")));
        Assert.NotNull(GameFilterPresets.Upsert(list, P("P3", p => p.Tag = "x")));
        Assert.Equal(GameFilterPresets.MaxPresets, GameFilterPresets.Sanitize(Enumerable.Range(0, 50).Select(i => P("n" + i))).Count);
    }

    [Fact]
    public void Hostile_values_fall_back_to_all_and_are_bounded()
    {
        var clean = GameFilterPresets.Normalize(P(new string('x', 200) + "\u0007", p =>
        {
            p.Sort = "drop table"; p.Source = "../../etc"; p.Installed = "maybe";
            p.Text = new string('t', 500); p.Tag = "a\u0000b";
        }))!;

        Assert.Equal(GameFilterPresets.MaxNameLength, clean.Name.Length);
        Assert.Equal("", clean.Sort);
        Assert.Equal("", clean.Source);
        Assert.Equal("", clean.Installed);
        Assert.Equal(200, clean.Text.Length);
        Assert.Equal("ab", clean.Tag);
    }

    [Fact]
    public void New_store_sources_are_accepted()
    {
        foreach (string source in new[] { "ea", "battlenet", "ubisoft", "xbox" })
            Assert.Equal(source, GameFilterPresets.Normalize(P("s", p => p.Source = source))!.Source);
    }

    [Fact]
    public void Remove_is_case_insensitive_and_missing_names_are_a_no_op()
    {
        var list = GameFilterPresets.Upsert(GameFilterPresets.Upsert(null, P("A")), P("B"))!;

        Assert.Equal(new[] { "B" }, GameFilterPresets.Remove(list, "a").Select(p => p.Name).ToArray());
        Assert.Equal(2, GameFilterPresets.Remove(list, "zzz").Count);
        Assert.Empty(GameFilterPresets.Remove(null, "a"));
    }

    [Fact]
    public void Sanitize_drops_invalid_and_duplicate_entries_from_hand_edited_settings()
    {
        var loaded = GameFilterPresets.Sanitize(new[] { P("A"), P(""), P("a"), null!, P("B") });
        Assert.Equal(new[] { "A", "B" }, loaded.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Older_settings_without_presets_load_as_empty_and_unknown_json_fields_are_ignored()
    {
        var back = JsonSerializer.Deserialize<List<GameFilterPreset>>("[{\"Name\":\"X\",\"FutureField\":1}]")!;
        Assert.Equal("X", Assert.Single(GameFilterPresets.Sanitize(back)).Name);
        Assert.Empty(GameFilterPresets.Sanitize(null));
    }
}
