using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameStoreLaunchIdPolicyTests
{
    [Theory]
    [InlineData("steam", "730", true)]
    [InlineData("steam", "730 -silent", false)]
    [InlineData("steam", "1234567890123456", false)]
    [InlineData("gog", "1207659123", true)]
    [InlineData("gog", "123456789012345678901234", true)]
    [InlineData("gog", "1234567890123456789012345", false)]
    [InlineData("epic", "Cobra", true)]
    [InlineData("epic", "game_2026.1", true)]
    [InlineData("epic", "game:bad", false)]
    [InlineData("epic", "game?launch=anything", false)]
    [InlineData("epic", "game/../../windows", false)]
    [InlineData("unknown", "730", false)]
    [InlineData(null, "730", false)]
    [InlineData("steam", null, false)]
    public void Only_a_supported_store_and_syntactically_safe_id_gets_a_launch_action(
        string? store, string? id, bool supported)
    {
        Assert.Equal(supported, GameStoreLaunchIdPolicy.SupportsOfficialLaunch(store, id));
    }

    [Fact]
    public void Validation_is_case_insensitive_for_providers_but_not_user_commands()
    {
        Assert.True(GameStoreLaunchIdPolicy.SupportsOfficialLaunch("StEaM", "730"));
        Assert.False(GameStoreLaunchIdPolicy.SupportsOfficialLaunch("StEaM", "-applaunch 730"));
    }
}
