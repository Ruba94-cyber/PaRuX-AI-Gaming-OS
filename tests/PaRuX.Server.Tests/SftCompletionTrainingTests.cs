using System.Reflection;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-level and pure dataset regressions for conversational SFT training
/// on assistant completion tokens rather than copying instruction prompt tokens.
/// Real tokenizer masks and multi-turn loss are native Windows acceptance tests.
/// </summary>
public sealed class SftCompletionTrainingTests
{
    private static string Line(params (string Role, string Content)[] turns) =>
        JsonSerializer.Serialize(new
        {
            messages = turns.Select(t => new { role = t.Role, content = t.Content }).ToArray()
        });

    [Fact]
    public void Standard_chat_with_user_then_assistant_is_suitable_for_SFT()
    {
        var shape = TrainingDatasetShape.Inspect(new[]
        {
            Line(("system", "Answer carefully"), ("user", "Explain CAN"), ("assistant", "CAN is a bus"))
        });
        Assert.True(shape.SupportsSupervisedFineTuning);
        Assert.Equal(1, shape.ValidChatRecords);
    }

    [Theory]
    [InlineData("user", "Question")]
    [InlineData("assistant", "Answer")]
    [InlineData("system", "System prompt")]
    public void A_log_without_a_supervised_user_to_assistant_target_is_not_SFT(string role, string content)
    {
        var shape = TrainingDatasetShape.Inspect(new[] { Line((role, content)) });
        Assert.False(shape.SupportsSupervisedFineTuning);
        Assert.Equal(0, shape.ValidChatRecords);
    }

    [Fact]
    public void Multi_turn_dataset_is_accepted_and_does_not_require_generation_template_markers()
    {
        var shape = TrainingDatasetShape.Inspect(new[]
        {
            Line(("user", "First"), ("assistant", "Reply one"),
                 ("user", "Second"), ("assistant", "Reply two"))
        });
        Assert.True(shape.SupportsSupervisedFineTuning);
    }

    [Fact]
    public void Pinned_runner_uses_conversational_prompt_completion_mask_for_SFT_only()
    {
        var source = typeof(TrainingLabService).GetField(
            "TrainScriptSource", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(source);
        string python = Assert.IsType<string>(source!.GetRawConstantValue());

        Assert.Contains("data.append({\"prompt\": list(history), \"completion\": [turn]})", python);
        Assert.Contains("completion_only_loss=(method == \"sft\")", python);
        Assert.Contains("if method == \"continued-pretraining\":", python);
        Assert.Contains("data = [{\"text\": r[cfg[\"text_field\"]]} for r in rows()]", python);
        Assert.DoesNotContain("tokenizer.apply_chat_template(r[\"messages\"]", python);
    }
}
