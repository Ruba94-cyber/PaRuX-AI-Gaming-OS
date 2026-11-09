using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DocumentMentionPolicyTests
{
    private static readonly (string Id, string FolderPath)[] Sources =
    {
        ("a", @"C:\Docs\Service Manuals 2025"),
        ("b", @"D:\Legal"),
        ("c", @"D:\Legal Archive"),
    };

    [Theory]
    [InlineData(@"C:\Docs\Service Manuals 2025", "service-manuals-2025")]
    [InlineData(@"D:\Legal\", "legal")]
    [InlineData(@"C:\Progetti\Relazione_Tecnica (v2)", "relazione-tecnica-v2")]
    public void Slug_is_the_folder_name_lowercased_with_dashes(string path, string expected) =>
        Assert.Equal(expected, DocumentMentionPolicy.Slug(path));

    [Fact]
    public void Exact_mention_scopes_to_that_folder_and_is_removed_from_the_question()
    {
        var r = DocumentMentionPolicy.Resolve("#service-manuals-2025 what is the torque spec?", Sources);
        Assert.Equal(new[] { "a" }, r.SourceIds);
        Assert.Equal("what is the torque spec?", r.CleanText);
    }

    [Fact]
    public void Unique_prefix_matches_but_an_ambiguous_prefix_does_not()
    {
        Assert.Equal(new[] { "a" }, DocumentMentionPolicy.Resolve("#service summarize", Sources).SourceIds);
        var ambiguous = DocumentMentionPolicy.Resolve("#leg summarize", Sources);
        Assert.Empty(ambiguous.SourceIds);
        Assert.Equal("#leg summarize", ambiguous.CleanText);
        Assert.Contains("leg", ambiguous.Unmatched);
    }

    [Fact]
    public void Exact_name_wins_over_a_longer_folder_sharing_the_prefix()
    {
        var r = DocumentMentionPolicy.Resolve("#legal is this clause valid?", Sources);
        Assert.Equal(new[] { "b" }, r.SourceIds);
    }

    [Fact]
    public void Several_mentions_scope_to_all_of_them()
    {
        var r = DocumentMentionPolicy.Resolve("compare #legal and #service-manuals-2025 on warranty", Sources);
        Assert.Equal(new[] { "b", "a" }, r.SourceIds);
        Assert.Equal("compare and on warranty", r.CleanText);
    }

    [Fact]
    public void Unknown_hashtags_and_urls_are_left_alone()
    {
        var r = DocumentMentionPolicy.Resolve("see https://example.org/page#legal and #nothing here", Sources);
        Assert.Empty(r.SourceIds);
        Assert.Equal("see https://example.org/page#legal and #nothing here", r.CleanText);
    }

    [Fact]
    public void A_message_made_only_of_a_mention_is_kept_so_there_is_still_a_question()
    {
        var r = DocumentMentionPolicy.Resolve("#legal", Sources);
        Assert.Equal(new[] { "b" }, r.SourceIds);
        Assert.Equal("#legal", r.CleanText);
    }

    [Fact]
    public void No_sources_or_no_hash_returns_the_text_unchanged()
    {
        Assert.Equal("hello", DocumentMentionPolicy.Resolve("hello", Sources).CleanText);
        Assert.Empty(DocumentMentionPolicy.Resolve("#legal x", Array.Empty<(string, string)>()).SourceIds);
    }
}
