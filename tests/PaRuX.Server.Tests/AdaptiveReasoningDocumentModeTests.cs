using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Found by the strict Corporate acceptance run: a plain document question containing the word
/// "code" was treated as a coding task and answered with hidden reasoning on a 4B CPU model.
/// </summary>
public sealed class AdaptiveReasoningDocumentModeTests
{
    private static ChatCompletionRequest Ask(string question, bool documents) => new()
    {
        Messages = { new ChatCompletionMessage { Role = "user", Content = question } },
        Documents = documents ? new ChatDocumentScope { Enabled = true } : null
    };

    [Theory]
    [InlineData("What is the access code for the Kestrel records vault? Answer briefly and cite the source filename.")]
    [InlineData("Which test applies to DTC P0420 in the repository of procedures?")]
    [InlineData("Qual è il codice della fattura INV_2026_884?")]
    public void A_plain_document_question_runs_without_hidden_reasoning(string question)
    {
        AdaptiveReasoningPolicy.Decision decision = AdaptiveReasoningPolicy.Decide(Ask(question, documents: true));

        Assert.Equal("minimal", decision.Effort);
        Assert.Equal(0, decision.Budget);
    }

    [Fact]
    public void The_same_words_outside_document_mode_keep_the_adaptive_decision()
    {
        const string question = "What is the access code for the Kestrel records vault? Answer briefly and cite the source filename.";

        Assert.Equal("coding", AdaptiveReasoningPolicy.InferRole(question));
        Assert.True(AdaptiveReasoningPolicy.Decide(Ask(question, documents: false)).Budget > 0);
    }

    [Theory]
    [InlineData("Explain why revision 3 of procedure 10-4500 changed the torque.")]
    [InlineData("Compare the two leave policies and describe the differences.")]
    public void A_document_question_that_asks_for_reasoning_is_not_forced_to_minimal(string question)
    {
        AdaptiveReasoningPolicy.Decision withDocuments = AdaptiveReasoningPolicy.Decide(Ask(question, documents: true));
        AdaptiveReasoningPolicy.Decision without = AdaptiveReasoningPolicy.Decide(Ask(question, documents: false));

        Assert.Equal(without.Effort, withDocuments.Effort);
        Assert.Equal(without.Budget, withDocuments.Budget);
    }
}
