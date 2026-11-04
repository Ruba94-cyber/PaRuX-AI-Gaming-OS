using System;
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
        Assert.Contains("CodingAgentPromptBuilder.EvidenceClosing(recent)", source);
        Assert.DoesNotContain("Large tool/file evidence is never intentionally discarded", source);
    }
}
