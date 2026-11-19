using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConversationMarkdownTests
{
    private static Conversation Chat(string title, DateTime created) => new()
    {
        Title = title,
        CreatedUtc = created,
        Messages = { new ChatMessage { Role = "user", Content = "hello" }, new ChatMessage { Role = "assistant", Content = "hi there" } },
    };

    [Fact]
    public void A_chat_becomes_readable_markdown_with_both_speakers()
    {
        string md = ConversationMarkdown.ToMarkdown(Chat("Trip plan", DateTime.UtcNow));
        Assert.StartsWith("# Trip plan", md, StringComparison.Ordinal);
        Assert.Contains("## You", md, StringComparison.Ordinal);
        Assert.Contains("## PaRuX", md, StringComparison.Ordinal);
        Assert.Contains("hi there", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b:c*d?e", "a_b_c_d_e")]
    [InlineData("  ", "PaRuX chat")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("name. ", "name")]
    public void File_names_are_safe_on_windows(string input, string expected) =>
        Assert.Equal(expected, ConversationMarkdown.SafeFileName(input));

    [Fact]
    public void A_very_long_title_is_cut_to_80_characters()
    {
        string name = ConversationMarkdown.SafeFileName(new string('x', 300));
        Assert.Equal(80, name.Length);
    }

    [Fact]
    public void Clashing_titles_get_numbered_names_and_nothing_overwrites_anything()
    {
        var day = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var plan = ConversationMarkdown.PlanFileNames(new[] { Chat("Same", day), Chat("SAME", day), Chat("Same", day.AddDays(1)), Chat("Other", day) });
        string[] names = plan.Select(p => p.FileName).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("2026-01-02 Same.md", names);
        Assert.Contains("2026-01-02 SAME (2).md", names);
        Assert.All(names, n => Assert.DoesNotContain(Path.DirectorySeparatorChar, n));
    }
}
