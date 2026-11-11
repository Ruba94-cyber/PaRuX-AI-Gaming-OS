using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// ROUTE-KW: keyword heuristics must not fire on substrings or on non-programming senses of a word
/// ("access code", "latest", "digital", "report", "improve") in plain chat; only document mode was fixed before.
/// </summary>
public sealed class RouteKeywordBoundaryTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "parux-route-kw-" + Guid.NewGuid().ToString("N"));

    public RouteKeywordBoundaryTests() => Directory.CreateDirectory(_stateDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_stateDirectory, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("What is the access code for the records vault?")]
    [InlineData("What is the promo code for the spring sale?")]
    [InlineData("Give me the postal code of Milan")]
    [InlineData("What are the latest news about electric cars?")]
    [InlineData("Show me a digital photo frame review")]
    [InlineData("Write a report about the weather")]
    [InlineData("How can I improve my sleep?")]
    [InlineData("Is that offer legit?")]
    public void Ordinary_chat_is_not_inferred_as_coding(string prompt)
    {
        Assert.NotEqual("coding", AdaptiveReasoningPolicy.InferRole(prompt));
    }

    [Theory]
    [InlineData("Refactor this class to remove the duplicated code")]
    [InlineData("Debug the failing unit test in the repo")]
    [InlineData("Write code that parses a CSV file")]
    [InlineData("Fix the compile error, then run git status")]
    [InlineData("Implement a binary search")]
    [InlineData("Review this source code for bugs")]
    public void Genuine_coding_requests_still_infer_coding(string prompt)
    {
        Assert.Equal("coding", AdaptiveReasoningPolicy.InferRole(prompt));
    }

    [Fact]
    public void A_skill_trigger_does_not_match_inside_a_longer_word()
    {
        var registry = new SkillRegistryService(_stateDirectory);
        SkillManifest created = registry.CreateOrUpdateUserSkill(new SkillManifest
        {
            DisplayName = "Word Tool",
            Category = "General",
            TriggerExamples = new List<string> { "word" }
        }, isImport: false);
        var router = new SkillRouter(registry);

        Assert.Equal("general", router.Route("What is the password of the vault?", null).Skill?.Id);
        Assert.Equal(created.Id, router.Route("Count each word in this text", null).Skill?.Id);
    }
}
