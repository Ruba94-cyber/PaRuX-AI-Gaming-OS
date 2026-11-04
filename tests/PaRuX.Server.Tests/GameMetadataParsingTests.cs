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
    [InlineData("https://attacker.akamaihd.net/a.png", false)]
    [InlineData("https://steamcdn-a.akamaihd.net/steam/apps/1/header.jpg", true)]
    [InlineData("file:///C:/secret.png", false)]
    public void Image_urls_are_limited_to_the_catalogue_cdns(string url, bool allowed) =>
        Assert.Equal(allowed, GameMetadataParsing.IsAllowedImageUrl(url));

    [Fact]
    public void SteamGridDb_search_gives_the_close_match_in_ranked_order()
    {
        const string json = """
        {"success":true,"data":[
          {"id":5254383,"name":"Ghost of Tsushima: Director's Cut","types":["steam"],"verified":true},
          {"id":5254384,"name":"Ghost of Tsushima","types":["steam"],"verified":true},
          {"id":99,"name":"Something Else","types":["epic"],"verified":false}]}
        """;
        List<(long Id, string Name)> found = GameMetadataParsing.ParseSteamGridDbSearch(json);
        Assert.Equal(3, found.Count);
        Assert.Equal(5254383, GameMetadataParsing.PickSteamGridDbGame("Ghost of Tsushima DC", found));
        Assert.Null(GameMetadataParsing.PickSteamGridDbGame("Hades II", found));
        Assert.Empty(GameMetadataParsing.ParseSteamGridDbSearch("""{"success":false,"errors":["Unauthorized"]}"""));
        Assert.Empty(GameMetadataParsing.ParseSteamGridDbSearch("not json"));
        Assert.Empty(GameMetadataParsing.ParseSteamGridDbSearch(null));
    }

    [Fact]
    public void SteamGridDb_grids_prefer_exact_portrait_then_score_and_skip_unsafe_or_animated()
    {
        const string json = """
        {"success":true,"page":0,"total":9,"limit":50,"data":[
          {"id":1,"score":90,"style":"alternate","width":920,"height":430,"nsfw":false,"humor":false,"mime":"image/png","url":"https://cdn2.steamgriddb.com/grid/wide.png"},
          {"id":2,"score":80,"style":"alternate","width":600,"height":900,"nsfw":true,"humor":false,"mime":"image/png","url":"https://cdn2.steamgriddb.com/grid/nsfw.png"},
          {"id":3,"score":70,"style":"alternate","width":600,"height":900,"nsfw":false,"humor":true,"mime":"image/png","url":"https://cdn2.steamgriddb.com/grid/humor.png"},
          {"id":4,"score":60,"style":"alternate","width":600,"height":900,"nsfw":false,"humor":false,"epilepsy":true,"mime":"image/webp","url":"https://cdn2.steamgriddb.com/grid/anim.webp"},
          {"id":5,"score":50,"style":"alternate","width":600,"height":900,"nsfw":false,"humor":false,"mime":"image/gif","url":"https://cdn2.steamgriddb.com/grid/anim.gif"},
          {"id":6,"score":40,"style":"alternate","width":600,"height":900,"nsfw":false,"humor":false,"mime":"image/png","url":"https://evil.example.com/grid/x.png"},
          {"id":7,"score":30,"style":"alternate","width":342,"height":482,"nsfw":false,"humor":false,"mime":"image/jpeg","url":"https://cdn.steamgriddb.com/grid/small-high.jpg"},
          {"id":8,"score":5,"style":"alternate","width":600,"height":900,"nsfw":false,"humor":false,"mime":"image/png","url":"https://cdn2.steamgriddb.com/grid/exact-low.png"},
          {"id":9,"score":12,"style":"alternate","width":600,"height":900,"nsfw":false,"humor":false,"mime":"image/jpeg","url":"https://cdn2.steamgriddb.com/grid/exact-best.jpg"}]}
        """;
        Assert.Equal("https://cdn2.steamgriddb.com/grid/exact-best.jpg", GameMetadataParsing.PickSteamGridDbGrid(json));
        // Without an exact 600x900 grid, the best-scored other portrait grid wins.
        string noExact = json.Replace("\"width\":600,\"height\":900", "\"width\":512,\"height\":768");
        Assert.Equal("https://cdn.steamgriddb.com/grid/small-high.jpg", GameMetadataParsing.PickSteamGridDbGrid(noExact));
    }

    [Fact]
    public void SteamGridDb_grids_return_null_when_nothing_is_usable()
    {
        Assert.Null(GameMetadataParsing.PickSteamGridDbGrid("""{"success":true,"data":[]}"""));
        Assert.Null(GameMetadataParsing.PickSteamGridDbGrid("""{"success":false,"errors":["Forbidden"]}"""));
        Assert.Null(GameMetadataParsing.PickSteamGridDbGrid("<html>"));
        Assert.Null(GameMetadataParsing.PickSteamGridDbGrid(
            """{"success":true,"data":[{"width":600,"height":900,"mime":"image/png","url":"http://cdn2.steamgriddb.com/a.png"}]}"""));
    }

    [Theory]
    [InlineData("https://cdn2.steamgriddb.com/grid/a.png", true)]
    [InlineData("https://cdn.steamgriddb.com/grid/a.png", true)]
    [InlineData("http://cdn2.steamgriddb.com/grid/a.png", false)]
    [InlineData("https://www.steamgriddb.com/grid/a.png", false)]
    [InlineData("https://cdn2.steamgriddb.com.evil.example/a.png", false)]
    [InlineData("https://user:pw@cdn2.steamgriddb.com/a.png", false)]
    public void SteamGridDb_images_come_only_from_its_two_cdn_hosts(string url, bool allowed)
    {
        Assert.Equal(allowed, GameMetadataParsing.IsSteamGridDbImageUrl(url));
        Assert.Equal(allowed, GameMetadataParsing.IsAllowedImageUrl(url));
    }
}
