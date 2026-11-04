using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameMetadataParsingTests
{
    [Theory]
    [InlineData("Ghost of Tsushima DC", "ghost tsushima")]
    [InlineData("Mario Kart 8 Deluxe[0100152000022000][US][v0]", "mario kart 8")]
    [InlineData("The Last of Us™ Part II Remastered", "last us part ii")]
    [InlineData("", "")]
    public void Titles_are_normalised_without_trademarks_release_tags_or_edition_words(string input, string expected) =>
        Assert.Equal(expected, GameMetadataParsing.NormalizeTitle(input));

    [Fact]
    public void Steam_search_picks_the_close_match_and_ignores_unrelated_results()
    {
        const string json = """
        {"total":3,"items":[
          {"type":"app","name":"Ghost of Tsushima DIRECTOR'S CUT","id":2215430},
          {"type":"bundle","name":"Ghost Bundle","id":7},
          {"type":"app","name":"Ghostrunner","id":1139900}]}
        """;
        var items = GameMetadataParsing.ParseSteamSearch(json);
        Assert.Equal(2, items.Count); // the bundle is skipped
        Assert.Equal(2215430, GameMetadataParsing.PickSteamMatch("Ghost of Tsushima DC", items));
        Assert.Null(GameMetadataParsing.PickSteamMatch("Completely Different Title", items));
        Assert.Empty(GameMetadataParsing.ParseSteamSearch("not json"));
    }

    [Fact]
    public void Steam_details_give_cover_genres_and_text_and_refuse_foreign_image_hosts()
    {
        const string json = """
        {"2215430":{"success":true,"data":{"name":"Ghost of Tsushima","short_description":"A samurai story.",
          "header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/2215430/header.jpg",
          "genres":[{"id":"1","description":"Action"},{"id":"25","description":"Adventure"}],
          "developers":["Sucker Punch Productions"],"release_date":{"coming_soon":false,"date":"16 May, 2024"}}}}
        """;
        GameInfoDto? info = GameMetadataParsing.ParseSteamDetails("ghost", 2215430, json);
        Assert.NotNull(info);
        Assert.Equal(new[] { "Action", "Adventure" }, info!.Genres);
        Assert.Equal("Sucker Punch Productions", info.Developer);
        Assert.NotNull(info.CoverUrl);
        Assert.Equal(GameMetadataParsing.SourceSteam, info.Source);

        string hostile = json.Replace("shared.akamai.steamstatic.com", "evil.example.com");
        Assert.Null(GameMetadataParsing.ParseSteamDetails("ghost", 2215430, hostile)!.CoverUrl);
        Assert.Null(GameMetadataParsing.ParseSteamDetails("ghost", 2215430, "{\"2215430\":{\"success\":false}}"));
    }

    [Fact]
    public void Wikipedia_summary_is_accepted_only_for_a_matching_video_game_page()
    {
        const string json = """
        {"title":"Mario Kart 8 Deluxe","description":"2017 video game","extract":"Mario Kart 8 Deluxe is a racing game.",
         "thumbnail":{"source":"https://upload.wikimedia.org/wikipedia/en/b/b5/MarioKart8Boxart.jpg"},
         "content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Mario_Kart_8_Deluxe"}}}
        """;
        GameInfoDto? info = GameMetadataParsing.ParseWikipediaSummary("mk8", "Mario Kart 8 Deluxe[0100152000022000][US][v0]", json);
        Assert.NotNull(info);
        Assert.StartsWith("https://upload.wikimedia.org/", info!.CoverUrl);
        Assert.Null(GameMetadataParsing.ParseWikipediaSummary("mk8", "Something Else Entirely", json));
        Assert.Null(GameMetadataParsing.ParseWikipediaSummary("mk8", "Mario Kart 8 Deluxe", json.Replace("video game", "city")));
    }

    [Fact]
    public void Wikipedia_search_keeps_only_video_game_pages()
    {
        const string json = """{"pages":[{"key":"Mario_Kart","title":"Mario Kart","description":"Racing video game series"},{"key":"Mario","title":"Mario","description":"Fictional character"}]}""";
        Assert.Equal(new[] { "Mario_Kart" }, GameMetadataParsing.ParseWikipediaSearch(json));
    }

    [Theory]
    [InlineData("https://upload.wikimedia.org/wikipedia/en/a.png", true)]
    [InlineData("https://shared.akamai.steamstatic.com/x.jpg", true)]
    [InlineData("http://upload.wikimedia.org/a.png", false)]
    [InlineData("https://evil.example.com/a.png", false)]
    [InlineData("https://user:pw@upload.wikimedia.org/a.png", false)]
    [InlineData("file:///C:/secret.png", false)]
    public void Image_urls_are_limited_to_the_catalogue_cdns(string url, bool allowed) =>
        Assert.Equal(allowed, GameMetadataParsing.IsAllowedImageUrl(url));
}
