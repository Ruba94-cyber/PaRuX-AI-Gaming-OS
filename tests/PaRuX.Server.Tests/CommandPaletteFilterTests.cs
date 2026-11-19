using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CommandPaletteFilterTests
{
    private static readonly (string Title, string Keywords)[] Items =
    {
        ("Go to Chat", "conversation talk"),
        ("Go to Model Hub", "models download"),
        ("Go to Settings", "preferences language theme"),
        ("New chat", "start conversation"),
        ("Toggle Console / TV mode", "fullscreen controller"),
    };

    private static string[] Run(string query) =>
        CommandPaletteFilter.Filter(Items, query, i => i.Title, i => i.Keywords).Select(i => i.Title).ToArray();

    [Fact]
    public void An_empty_query_lists_everything_in_the_given_order() => Assert.Equal(5, Run("").Length);

    [Fact]
    public void Every_typed_word_must_start_a_word_and_title_hits_come_before_keyword_hits()
    {
        Assert.Equal(new[] { "Go to Model Hub" }, Run("mod hub"));
        string[] conversation = Run("conv");
        Assert.Equal(2, conversation.Length); // both match only through a keyword
        Assert.Equal(new[] { "Go to Settings" }, Run("lang"));
        Assert.Empty(Run("zzz"));
    }

    [Fact]
    public void A_title_word_outranks_a_keyword_only_match()
    {
        string[] chat = Run("chat");
        Assert.Equal("Go to Chat", chat[0]);
        Assert.Equal("New chat", chat[1]);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_ignores_punctuation_in_titles() =>
        Assert.Contains("Toggle Console / TV mode", Run("TV"));
}
