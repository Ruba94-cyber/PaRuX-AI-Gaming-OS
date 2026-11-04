using System;
using System.Collections.Generic;
using System.IO;
using PaRuX.Services;
using Xunit;

public class CodingAgentEvidenceClosingTests
{
    [Fact]
    public void Plain_evidence_gets_no_artifact_paragraph_that_invites_rereading()
    {
        string closing = CodingAgentPromptBuilder.EvidenceClosing(new[]
        {
            "run_command \"python -m unittest\" (exit 1)\nAssertionError: 3.0 != 4",
            "read_file \"calc.py\"\ndef average(values):\n    return sum(values) / (len(values) + 1)"
        });

        Assert.DoesNotContain("artifact", closing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncated preview", closing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never repeat an action whose result is shown", closing);
        Assert.Contains("replace_range", closing);
        Assert.EndsWith("What is the next single JSON action?", closing);
    }

    [Theory]
    [InlineData("read_file \"big.cs\"\n...\n[FULL FILE artifactId=abc123; chars=90000]")]
    [InlineData("run_command x\nhead\n[Middle omitted from prompt; full observation remains in checkpoint/artifact.]\ntail")]
    public void Truncated_or_artifact_evidence_still_points_at_artifact_tools(string evidence)
    {
        string closing = CodingAgentPromptBuilder.EvidenceClosing(new[] { evidence });

        Assert.Contains("read_artifact/search_artifact/list_artifacts", closing);
        Assert.Contains("Never repeat an action whose result is shown", closing);
    }

    [Fact]
    public void Empty_evidence_is_safe()
    {
        string closing = CodingAgentPromptBuilder.EvidenceClosing(Array.Empty<string>());
        Assert.DoesNotContain("artifactId", closing, StringComparison.Ordinal);
        Assert.Contains("next single JSON action", closing);
    }

    [Fact]
    public void Service_uses_the_conditional_closing_instead_of_the_unconditional_artifact_paragraph()
    {
        string dir = AppContext.BaseDirectory;
        string? root = dir;
        while (root != null && !File.Exists(Path.Combine(root, "src", "PaRuX.App", "Services", "CodingAgentService.cs")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root!, "src", "PaRuX.App", "Services", "CodingAgentService.cs"));
        Assert.Contains("CodingAgentPromptBuilder.EvidenceClosing(recent, verificationRequired && verificationSatisfied)", source);
        Assert.DoesNotContain("Large tool/file evidence is never intentionally discarded", source);
    }

    [Fact]
    public void Passed_verification_makes_done_the_obvious_next_action_without_replacing_the_gate()
    {
        string closing = CodingAgentPromptBuilder.EvidenceClosing(new[] { "run_command \"python -m unittest\" (exit 0)\nOK" }, verificationPassedAfterChange: true);
        Assert.Contains("\"action\":\"done\"", closing);
        Assert.Contains("Do not run the same verification command again", closing);
        Assert.EndsWith("What is the next single JSON action?", closing);

        string without = CodingAgentPromptBuilder.EvidenceClosing(new[] { "x" }, verificationPassedAfterChange: false);
        Assert.DoesNotContain("\"action\":\"done\"", without);
    }

    [Fact]
    public void Ledger_that_only_duplicates_the_verbatim_evidence_is_dropped()
    {
        var log = new[] { "run_command exit 1\nFAIL", "read_file calc.py\ndef average(values): ..." };
        string ledger = "[VIRTUAL_CONTEXT_V2_LEDGER]\nActive constraints\n- run_command exit 1\n[/VIRTUAL_CONTEXT_V2_LEDGER]";
        var (recent, memory) = CodingAgentPromptBuilder.SelectEvidence(log, 12, 4000, ledger);
        Assert.Equal(2, recent.Count);
        Assert.Equal(string.Empty, memory);
    }

    [Fact]
    public void Ledger_is_kept_when_older_observations_no_longer_fit_verbatim()
    {
        var log = new List<string>();
        for (int i = 0; i < 20; i++) log.Add("observation " + i + " " + new string('x', 400));
        string ledger = "[VIRTUAL_CONTEXT_V2_LEDGER]\nolder facts\n[/VIRTUAL_CONTEXT_V2_LEDGER]";
        var (recent, memory) = CodingAgentPromptBuilder.SelectEvidence(log, 12, 2000, ledger);
        Assert.True(recent.Count < log.Count);
        Assert.Equal(ledger, memory);
    }

    [Fact]
    public void Ledger_budget_returns_to_the_tail_when_that_lets_everything_fit()
    {
        var log = new[] { new string('a', 600), new string('b', 600) };
        string ledger = new string('m', 700);
        var (recent, memory) = CodingAgentPromptBuilder.SelectEvidence(log, 12, 1000, ledger);
        Assert.Equal(2, recent.Count);
        Assert.Equal(string.Empty, memory);
    }

    [Fact]
    public void Repository_context_does_not_receive_observations_that_are_replayed_verbatim()
    {
        var log = new List<string> { "run_command exit 1", "read_file calc.py\ndef average(): ..." };
        Assert.Null(CodingAgentPromptBuilder.ObservationsBeyondVerbatimTail(log, 12));

        var many = new List<string>();
        for (int i = 0; i < 15; i++) many.Add("obs" + i);
        var older = CodingAgentPromptBuilder.ObservationsBeyondVerbatimTail(many, 12)!;
        Assert.Equal(new[] { "obs0", "obs1", "obs2" }, older);
    }

    [Fact]
    public void Service_never_passes_the_whole_log_as_prior_observations()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "src", "PaRuX.App", "Services", "CodingAgentService.cs")))
            root = Path.GetDirectoryName(root);
        string source = File.ReadAllText(Path.Combine(root!, "src", "PaRuX.App", "Services", "CodingAgentService.cs"));
        Assert.DoesNotContain("priorObservations: log", source);
        Assert.Contains("ObservationsBeyondVerbatimTail(log, MaxLogEntriesInPrompt)", source);
    }
}
