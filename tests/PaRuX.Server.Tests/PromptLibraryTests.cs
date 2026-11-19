using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PromptLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-prompts-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "prompts.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Placeholders_are_found_once_in_order_and_filled_case_insensitively()
    {
        const string text = "Translate {{ text }} into {{language}}; again {{TEXT}} and {{language}}.";
        Assert.Equal(new[] { "text", "language" }, PromptTemplate.Variables(text));
        string filled = PromptTemplate.Fill(text, new Dictionary<string, string> { ["Text"] = "ciao", ["language"] = "English" });
        Assert.Equal("Translate ciao into English; again ciao and English.", filled);
    }

    [Fact]
    public void A_placeholder_without_a_value_stays_visible_instead_of_vanishing()
    {
        Assert.Equal("Hi {{name}}", PromptTemplate.Fill("Hi {{name}}", new Dictionary<string, string> { ["name"] = "" }));
        Assert.Empty(PromptTemplate.Variables("no variables { here }"));
    }

    [Fact]
    public void Saving_the_same_title_replaces_the_prompt_and_everything_survives_a_reopen()
    {
        var store = new PromptLibraryStore(FilePath);
        SavedPrompt first = store.Save("Standup", "Summarize {{notes}}")!;
        SavedPrompt second = store.Save("standup", "Summarize {{notes}} in 3 bullets")!;
        Assert.Equal(first.Id, second.Id);
        IReadOnlyList<SavedPrompt> reopened = new PromptLibraryStore(FilePath).List();
        Assert.Single(reopened);
        Assert.Equal("Summarize {{notes}} in 3 bullets", reopened[0].Text);
        Assert.True(store.Remove(first.Id));
        Assert.Empty(new PromptLibraryStore(FilePath).List());
        Assert.False(store.Remove("missing"));
    }

    [Fact]
    public void Invalid_titles_texts_and_a_full_library_are_refused_without_writing()
    {
        var store = new PromptLibraryStore(FilePath);
        Assert.Null(store.Save("", "x"));
        Assert.Null(store.Save("t", "   "));
        Assert.Null(store.Save(new string('t', PromptLibraryStore.MaxTitle + 1), "x"));
        Assert.Null(store.Save("t", new string('x', PromptLibraryStore.MaxText + 1)));
        Assert.Null(store.Save("bad\ttitle", "x"));
        Assert.False(File.Exists(FilePath));
        for (int i = 0; i < PromptLibraryStore.MaxPrompts; i++) Assert.NotNull(store.Save("p" + i, "x"));
        Assert.Null(store.Save("one too many", "x"));
        Assert.NotNull(store.Save("p0", "replaced")); // replacing at the limit is still allowed
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty_and_can_be_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var store = new PromptLibraryStore(FilePath);
        Assert.Empty(store.List());
        Assert.NotNull(store.Save("fresh", "text"));
        Assert.Single(store.List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_starter_prompt_has_a_title_a_placeholder_and_a_stable_id(bool italian)
    {
        IReadOnlyList<SavedPrompt> starters = StarterPrompts.All(italian);
        Assert.True(starters.Count >= 6);
        Assert.Equal(starters.Count, starters.Select(s => s.Id).Distinct().Count());
        Assert.All(starters, s => { Assert.NotEmpty(s.Title); Assert.NotEmpty(PromptTemplate.Variables(s.Text)); });
    }
}
