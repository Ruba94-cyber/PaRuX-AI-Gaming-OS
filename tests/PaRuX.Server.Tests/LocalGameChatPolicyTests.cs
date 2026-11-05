using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalGameChatPolicyTests
{
    [Fact]
    public void Uses_only_explicitly_selected_source_ids_in_existing_Chat_scope()
    {
        var result = LocalGameChatPolicy.Build("Example Quest", "Where is the manual save point?",
            true, new[] { "guide-1", "guide-1", "manual-2" });
        Assert.True(result.Ready);
        Assert.NotNull(result.Documents);
        Assert.True(result.Documents!.Enabled);
        Assert.Equal(new[] { "guide-1", "manual-2" }, result.Documents.SourceIds);
        Assert.Contains("Example Quest", result.QuestionForChat);
    }

    [Fact]
    public void No_consent_or_empty_sources_never_searches_all_documents()
    {
        Assert.False(LocalGameChatPolicy.Build("Game", "Help", false, new[] { "private" }).Ready);
        var empty = LocalGameChatPolicy.Build("Game", "Help", true, System.Array.Empty<string>());
        Assert.False(empty.Ready);
        Assert.Null(empty.Documents);
        Assert.Equal(string.Empty, empty.QuestionForChat);
    }

    [Fact]
    public void Game_priority_wins_without_explicit_user_action()
    {
        var decision = GameGpuAdmissionPolicy.Evaluate(true, true, false);
        Assert.Equal(GameGpuAdmission.BlockedByGaming, decision.Status);
        Assert.Equal(GameGpuAdmission.RequiresUserAction,
            GameGpuAdmissionPolicy.Evaluate(true, true, true).Status);
        Assert.Equal(GameGpuAdmission.Allowed,
            GameGpuAdmissionPolicy.Evaluate(false, false, false).Status);
    }

    [Fact]
    public void Even_explicit_override_does_not_auto_start_ai_while_game_runs()
    {
        var decision = GameGpuAdmissionPolicy.Evaluate(false, true, true);
        Assert.NotEqual(GameGpuAdmission.Allowed, decision.Status);
    }
}
