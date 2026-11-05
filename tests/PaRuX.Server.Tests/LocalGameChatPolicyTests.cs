using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalGameChatPolicyTests
{
    [Fact]
    public void Uses_only_explicitly_selected_source_ids_in_existing_Chat_scope()
    {
        var result = LocalGameChatPolicy.Build("Example Quest", "Where is the manual save point?",
            true, new[] { "guide-1", "guide-1", "manual-2" },
            new[] { "guide-1", "manual-2", "unselected-public-guide" });
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
    public void Selected_sources_must_belong_to_the_separately_verified_local_index()
    {
        var missingVerification = LocalGameChatPolicy.Build("Example Quest", "Where?",
            true, new[] { "guide-1" });
        Assert.False(missingVerification.Ready);
        Assert.Null(missingVerification.Documents);

        var outsideIndex = LocalGameChatPolicy.Build("Example Quest", "Where?",
            true, new[] { "guide-1", "private-guide" }, new[] { "guide-1" });
        Assert.False(outsideIndex.Ready);
        Assert.Null(outsideIndex.Documents);

        var approvedSubset = LocalGameChatPolicy.Build("Example Quest", "Where?",
            true, new[] { "guide-1" }, new[] { "guide-1", "unselected-guide" });
        Assert.True(approvedSubset.Ready);
        Assert.Equal(new[] { "guide-1" }, approvedSubset.Documents!.SourceIds);
    }

    [Theory]
    [InlineData("Show the map\\0and secrets")]
    [InlineData("Where is the key?\\u0001")]
    public void Game_guide_rejects_non_text_control_characters(string escaped)
    {
        string question = escaped.Replace("\\0", "\0").Replace("\\u0001", "\u0001");
        Assert.Throws<System.IO.InvalidDataException>(() => LocalGameChatPolicy.Build(
            "Synthetic Quest", question, true, new[] { "guide" }, new[] { "guide" }));
    }

    [Fact]
    public void Game_guide_preserves_multiline_questions_without_widening_scope()
    {
        var result = LocalGameChatPolicy.Build("Synthetic Quest",
            "Where is the map?\nAnd the key?", true, new[] { "guide" }, new[] { "guide", "other" });
        Assert.True(result.Ready);
        Assert.Contains("And the key?", result.QuestionForChat);
        Assert.Equal(new[] { "guide" }, result.Documents!.SourceIds);
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
    public void Unknown_game_telemetry_never_becomes_permission_to_use_gpu()
    {
        Assert.Equal(GameGpuAdmission.BlockedByGaming,
            GameGpuAdmissionPolicy.Evaluate((bool?)null, false, false).Status);
        Assert.Equal(GameGpuAdmission.BlockedByGaming,
            GameGpuAdmissionPolicy.Evaluate(false, (bool?)null, true).Status);
        Assert.Equal(GameGpuAdmission.BlockedByGaming,
            GameGpuAdmissionPolicy.Evaluate((bool?)null, (bool?)null, true).Status);
        Assert.Equal(GameGpuAdmission.Allowed,
            GameGpuAdmissionPolicy.Evaluate(false, false, false).Status);
    }

    [Fact]
    public void Even_explicit_override_does_not_auto_start_ai_while_game_runs()
    {
        var decision = GameGpuAdmissionPolicy.Evaluate(false, true, true);
        Assert.NotEqual(GameGpuAdmission.Allowed, decision.Status);
    }
    [Fact]
    public void Malformed_or_oversized_local_knowledge_scope_is_refused_before_Chat()
    {
        var malformed = LocalGameChatPolicy.Build(
            "Example Quest", "Where is the guide?", true, new[] { "guide-1", "invalid\nsource" });
        Assert.False(malformed.Ready);
        Assert.Null(malformed.Documents);
        var tooMany = LocalGameChatPolicy.Build(
            "Example Quest", "Where is the guide?", true,
            Enumerable.Range(0, 65).Select(i => "synthetic-source-" + i).ToArray());
        Assert.False(tooMany.Ready);
        Assert.Null(tooMany.Documents);
    }

}
