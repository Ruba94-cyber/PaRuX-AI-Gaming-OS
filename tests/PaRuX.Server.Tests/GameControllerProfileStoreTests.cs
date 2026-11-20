using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameControllerProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-gamepad-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "game-controller-profiles.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static GamingControllerProfile Inverted()
    {
        return GamingControllerProfile.Default with
        {
            InvertLeftY = true,
            ButtonRemap = new Dictionary<ushort, ushort> { [0x1000] = 0x2000, [0x2000] = 0x1000 },   // swap A and B
        };
    }

    [Fact]
    public void A_games_own_profile_is_saved_and_survives_a_reopen()
    {
        var store = new GameControllerProfileStore(FilePath);
        Assert.Null(store.Get("steam:1"));
        Assert.Null(store.Save("steam:1", Inverted()));

        var reopened = new GameControllerProfileStore(FilePath);
        GamingControllerProfile? loaded = reopened.Get("steam:1");
        Assert.NotNull(loaded);
        Assert.True(loaded!.InvertLeftY);
        Assert.Equal(0x2000, loaded.ButtonRemap[0x1000]);
        Assert.Null(reopened.Get("steam:2"));
    }

    [Fact]
    public void An_invalid_profile_or_key_is_refused_and_nothing_is_stored()
    {
        var store = new GameControllerProfileStore(FilePath);
        GamingControllerProfile bad = GamingControllerProfile.Default with { LeftDeadzone = GamingControllerProfile.MaxDeadzone + 1 };
        Assert.NotNull(store.Save("steam:1", bad));
        Assert.NotNull(store.Save("", Inverted()));
        Assert.NotNull(store.Save(new string('k', GameControllerProfileStore.MaximumKeyLength + 1), Inverted()));
        Assert.NotNull(store.Save("bad\u0007key", Inverted()));
        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Remove_returns_a_game_to_the_global_profile()
    {
        var store = new GameControllerProfileStore(FilePath);
        store.Save("gog:7", Inverted());
        Assert.True(store.Remove("gog:7"));
        Assert.False(store.Remove("gog:7"));
        Assert.Null(new GameControllerProfileStore(FilePath).Get("gog:7"));
    }

    [Fact]
    public void The_number_of_per_game_profiles_is_bounded_but_existing_ones_can_still_change()
    {
        var store = new GameControllerProfileStore(FilePath);
        for (int i = 0; i < GameControllerProfileStore.MaximumProfiles; i++)
            Assert.Null(store.Save("g" + i, Inverted()));
        Assert.NotNull(store.Save("one-too-many", Inverted()));
        Assert.Null(store.Save("g0", GamingControllerProfile.Default));
    }

    [Fact]
    public void A_damaged_file_means_no_per_game_profiles_and_is_not_overwritten_by_reading()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var store = new GameControllerProfileStore(FilePath);
        Assert.Equal(0, store.Count);
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }
}
