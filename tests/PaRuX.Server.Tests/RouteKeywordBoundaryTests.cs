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

    [Theory]
    [InlineData("What is the access  code of the vault?")]
    [InlineData("Send me the promo-code")]
    [InlineData("What is my zip-code?")]
    public void Code_qualifiers_tolerate_extra_spaces_and_hyphens(string prompt)
    {
        Assert.NotEqual("coding", AdaptiveReasoningPolicy.InferRole(prompt));
    }

    [Theory]
    [InlineData("I am testing the login page")]
    [InlineData("The build tested fine on GitHub")]
    [InlineData("Add it to the .gitignore and push to GitLab")]
    [InlineData("请debug这段程序")]
    public void Inflected_and_glued_coding_keywords_still_infer_coding(string prompt)
    {
        Assert.Equal("coding", AdaptiveReasoningPolicy.InferRole(prompt));
    }

    [Theory]
    [InlineData("summarizing", "Please keep summarizing this text")]
    [InlineData("invoice", "Show me the invoices from May")]
    [InlineData("fattura", "Controlla la fatturazione di maggio")]
    [InlineData("riassumi", "Riassumilo per favore")]
    [InlineData("翻译", "请翻译这段话")]
    public void A_skill_trigger_matches_inflections_and_cjk_text(string trigger, string message)
    {
        var registry = new SkillRegistryService(_stateDirectory);
        SkillManifest created = registry.CreateOrUpdateUserSkill(new SkillManifest
        {
            DisplayName = "Trig " + Guid.NewGuid().ToString("N")[..6],
            Category = "General",
            TriggerExamples = new List<string> { trigger }
        }, isImport: false);
        var router = new SkillRouter(registry);

        Assert.Equal(created.Id, router.Route(message, null).Skill?.Id);
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
