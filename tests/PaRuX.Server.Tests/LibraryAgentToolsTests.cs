using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LibraryAgentToolsTests
{
    private sealed class FakeBackend : IAgentLibraryBackend
    {
        public bool GamingActive { get; set; }
        public List<GameTitleDto> Items = new()
        {
            Title("Hades", "steam", "1145360", favorite: false, played: new DateTime(2026, 5, 1)),
            Title("Celeste", "manual", "m1", favorite: true, played: new DateTime(2026, 6, 1)),
            Title("Hollow Knight", "steam", "367520", favorite: false, played: null),
            Title("Hollow Knight Silksong", "steam", "1030300", favorite: false, played: null),
        };
        public List<string> Calls = new();
        public string? DownloadFailure;
        public string LaunchMessage = @"Started C:\Users\paolo\Games\Celeste\Celeste.exe";
        public bool Launched = true;
        public IReadOnlyList<GameTitleDto> Titles() => Items;
        public bool SetFavorite(string k, bool f) { Calls.Add($"fav:{k}:{f}"); return true; }
        public bool SetTags(string k, IReadOnlyList<string> t) { Calls.Add($"tags:{k}:{string.Join('|', t)}"); return true; }
        public bool SetCollections(string k, IReadOnlyList<string> c) { Calls.Add($"col:{k}:{string.Join('|', c)}"); return true; }
        public Task<ManualGameLaunchResult> LaunchAsync(GameInstallationDto i, CancellationToken ct)
        { Calls.Add("launch:" + i.InstallationId); return Task.FromResult(new ManualGameLaunchResult(Launched, false, LaunchMessage)); }
        public bool RemoveManualEntry(string id) { Calls.Add("remove:" + id); return true; }
        public string EnqueueDownload(string url, string? f, string? s)
        {
            if (DownloadFailure != null) throw new PaRuX.Server.Orchestration.Gaming.Downloads.GameDownloadException(DownloadFailure);
            Calls.Add("download:" + url);
            return "dl1";
        }
    }

    private static GameTitleDto Title(string name, string source, string id, bool favorite, DateTime? played)
    {
        string key = GameTitleCatalog.KeyFor(name, source + ":" + id);
        var inst = new GameInstallationDto(source + ":" + id, source, id, ManualEntryKinds.Game, name);
        return new GameTitleDto(key, name, favorite, new[] { "indie" }, 3600, played, inst.InstallationId, new[] { inst });
    }

    private static (AgentLibraryTools Tools, FakeBackend Backend, AgentLibraryConsentStore Consent) Make()
    {
        var backend = new FakeBackend();
        var consent = new AgentLibraryConsentStore();
        return (new AgentLibraryTools(backend, consent), backend, consent);
    }

    private static Task<string> Call(AgentLibraryTools t, string tool, string json, bool bypass = false, IReadOnlyCollection<string>? allowed = null) =>
        t.CallAsync(tool, json, CancellationToken.None, allowed, bypass);

    private static string IdFrom(string text)
    {
        int at = text.IndexOf("confirmation_id \"", StringComparison.Ordinal) + "confirmation_id \"".Length;
        return text[at..text.IndexOf('"', at)];
    }

    [Fact]
    public void Schemas_cover_every_tool_and_close_the_argument_set()
    {
        var (tools, _, _) = Make();
        JsonArray schemas = tools.GetToolSchemas();
        var names = schemas.Select(s => s!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(9, names.Count);
        foreach (JsonNode? s in schemas)
            Assert.False(s!["function"]!["parameters"]!["additionalProperties"]!.GetValue<bool>());
        Assert.All(names, n => Assert.True(AgentLibraryTools.IsBuiltInTool(n)));
    }

    [Fact]
    public void Skill_allow_list_hides_and_blocks_tools()
    {
        var (tools, b, _) = Make();
        var allowed = new[] { "library_search" };
        Assert.Single(tools.GetToolSchemas(allowed));
        string r = Call(tools, "library_set_favorite", """{"title":"Hades","favorite":true}""", allowed: allowed).Result;
        Assert.StartsWith("Library tool failed:", r);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task Search_filters_and_never_returns_a_path()
    {
        var (tools, b, _) = Make();
        b.Items[1] = b.Items[1] with { Name = @"Celeste C:\Users\paolo\Secret\x.exe" };
        string r = await Call(tools, "library_search", """{"query":"cel"}""");
        Assert.Contains("Celeste", r);
        Assert.DoesNotContain(@"C:\Users", r);
        Assert.DoesNotContain("paolo", r);
        string fav = await Call(tools, "library_search", """{"favorite":true}""");
        Assert.DoesNotContain("Hades", fav);
        string limited = await Call(tools, "library_search", """{"query":"hollow","limit":1}""");
        Assert.Contains("more exist", limited);
    }

    [Fact]
    public async Task Recent_orders_by_last_played_and_get_describes_one_title()
    {
        var (tools, _, _) = Make();
        string r = await Call(tools, "library_recent", "{}");
        Assert.True(r.IndexOf("Celeste", StringComparison.Ordinal) < r.IndexOf("Hades", StringComparison.Ordinal));
        Assert.DoesNotContain("Hollow", r);
        string g = await Call(tools, "library_get", """{"title":"hades"}""");
        Assert.Contains("tags: indie", g);
        Assert.Contains("play time: 1.0 h", g);
    }

    [Fact]
    public async Task Ambiguous_and_unknown_titles_are_reported_not_guessed()
    {
        var (tools, _, _) = Make();
        string amb = await Call(tools, "library_get", """{"title":"hollow"}""");
        Assert.StartsWith("Library tool failed:", amb);
        Assert.Contains("several titles", amb);
        string none = await Call(tools, "library_get", """{"title":"zzzzzz"}""");
        Assert.Contains("no library title matches", none);
    }

    [Fact]
    public async Task Reversible_edits_run_without_confirmation()
    {
        var (tools, b, _) = Make();
        Assert.StartsWith("OK", await Call(tools, "library_set_favorite", """{"title":"Hades","favorite":true}"""));
        Assert.StartsWith("OK", await Call(tools, "library_set_tags", """{"title":"Hades","tags":["a","b"]}"""));
        Assert.StartsWith("OK", await Call(tools, "library_set_collection", """{"title":"Hades","collections":["Roguelikes"]}"""));
        Assert.Equal(3, b.Calls.Count);
        Assert.Contains("tags:hades:a|b", b.Calls);
    }

    [Theory]
    [InlineData("library_launch", """{"title":"Celeste"}""")]
    [InlineData("library_remove_entry", """{"title":"Celeste"}""")]
    [InlineData("library_enqueue_download", """{"url":"https://example.com/a.zip"}""")]
    public async Task Dangerous_tools_need_confirmation_even_in_bypass_mode(string tool, string json)
    {
        var (tools, b, consent) = Make();
        Assert.True(AgentLibraryTools.RequiresConfirmation(tool));
        string first = await Call(tools, tool, json, bypass: true);
        Assert.Contains("Confirmation required", first);
        Assert.Empty(b.Calls);

        // The model cannot approve its own request, nor guess an id.
        string forged = await Call(tools, tool, json.Replace("}", ",\"confirmation_id\":\"deadbeef\"}"), bypass: true);
        Assert.Contains("Confirmation required", forged);
        string id = IdFrom(first);
        string unapproved = await Call(tools, tool, json.Replace("}", $",\"confirmation_id\":\"{id}\"}}"), bypass: true);
        Assert.Contains("Confirmation required", unapproved);
        Assert.Empty(b.Calls);

        Assert.True(consent.Approve(id));
        string done = await Call(tools, tool, json.Replace("}", $",\"confirmation_id\":\"{id}\"}}"), bypass: true);
        Assert.DoesNotContain("Confirmation required", done);
        Assert.Single(b.Calls);

        // Single use: replaying the same id asks again.
        string replay = await Call(tools, tool, json.Replace("}", $",\"confirmation_id\":\"{id}\"}}"), bypass: true);
        Assert.Contains("Confirmation required", replay);
        Assert.Single(b.Calls);
    }

    [Fact]
    public async Task Approval_is_bound_to_the_exact_request()
    {
        var (tools, b, consent) = Make();
        string first = await Call(tools, "library_launch", """{"title":"Celeste"}""");
        string id = IdFrom(first);
        consent.Approve(id);
        string other = await Call(tools, "library_launch", $$"""{"title":"Hades","confirmation_id":"{{id}}"}""");
        Assert.Contains("Confirmation required", other);
        Assert.DoesNotContain(b.Calls, c => c.StartsWith("launch:"));
        string wrongTool = await Call(tools, "library_remove_entry", $$"""{"title":"Celeste","confirmation_id":"{{id}}"}""");
        Assert.Contains("Confirmation required", wrongTool);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void Denied_or_expired_approvals_are_not_usable()
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = new AgentLibraryConsentStore(() => now);
        string a = store.Request("x", "b", "s")!;
        Assert.True(store.Deny(a));
        Assert.False(store.TryConsume(a, "x", "b"));
        string c = store.Request("x", "b2", "s")!;
        store.Approve(c);
        now = now.AddMinutes(6);
        Assert.False(store.TryConsume(c, "x", "b2"));
    }

    [Fact]
    public async Task Launch_result_is_redacted()
    {
        var (tools, b, consent) = Make();
        string id = IdFrom(await Call(tools, "library_launch", """{"title":"Celeste"}"""));
        consent.Approve(id);
        string r = await Call(tools, "library_launch", $$"""{"title":"Celeste","confirmation_id":"{{id}}"}""");
        Assert.Contains("[path]", r);
        Assert.DoesNotContain("paolo", r);
        Assert.Contains("launch:manual:m1", b.Calls);
    }

    [Fact]
    public async Task Store_installs_cannot_be_removed()
    {
        var (tools, b, _) = Make();
        string r = await Call(tools, "library_remove_entry", """{"title":"Hades"}""");
        Assert.StartsWith("Library tool failed:", r);
        Assert.Empty(b.Calls);
    }

    [Theory]
    [InlineData("library_set_favorite", """{"title":"Hades","favorite":true}""")]
    [InlineData("library_set_tags", """{"title":"Hades","tags":["a"]}""")]
    [InlineData("library_set_collection", """{"title":"Hades","collections":["a"]}""")]
    [InlineData("library_launch", """{"title":"Celeste"}""")]
    [InlineData("library_remove_entry", """{"title":"Celeste"}""")]
    [InlineData("library_enqueue_download", """{"url":"https://example.com/a.zip"}""")]
    public async Task Gaming_hard_off_refuses_state_changes_but_not_reads(string tool, string json)
    {
        var (tools, b, consent) = Make();
        b.GamingActive = true;
        string r = await Call(tools, tool, json);
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("Gaming Mode", r);
        Assert.Empty(b.Calls);
        Assert.Empty(consent.List());
        Assert.Contains("Hades", await Call(tools, "library_search", """{"query":"hades"}"""));
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "gaming-hard-off");
    }

    [Fact]
    public async Task Download_refusals_come_from_the_manager_and_are_redacted()
    {
        var (tools, b, consent) = Make();
        b.DownloadFailure = @"Only https addresses are accepted. See C:\Users\paolo\x";
        string url = """{"url":"magnet:?xt=urn:btih:abc"}""";
        string id = IdFrom(await Call(tools, "library_enqueue_download", url));
        consent.Approve(id);
        string r = await Call(tools, "library_enqueue_download", """{"url":"magnet:?xt=urn:btih:abc","confirmation_id":"__ID__"}""".Replace("__ID__", id));
        Assert.StartsWith("Library tool failed:", r);
        Assert.DoesNotContain("paolo", r);
    }

    [Theory]
    [InlineData("library_search", "not json")]
    [InlineData("library_search", "[]")]
    [InlineData("library_search", """{"query":5}""")]
    [InlineData("library_search", """{"unknown":"x"}""")]
    [InlineData("library_search", """{"limit":999}""")]
    [InlineData("library_get", "{}")]
    [InlineData("library_get", """{"title":""}""")]
    [InlineData("library_set_favorite", """{"title":"Hades","favorite":"yes"}""")]
    [InlineData("library_set_tags", """{"title":"Hades","tags":"a"}""")]
    [InlineData("library_set_tags", """{"title":"Hades","tags":[1]}""")]
    [InlineData("library_set_collection", """{"title":"Hades","collections":["a\u0001"]}""")]
    [InlineData("library_launch", """{"title":"Celeste","extra":1}""")]
    public async Task Malformed_arguments_are_rejected_without_side_effects(string tool, string json)
    {
        var (tools, b, consent) = Make();
        string r = await Call(tools, tool, json);
        Assert.StartsWith("Library tool failed:", r);
        Assert.Empty(b.Calls);
        Assert.Empty(consent.List());
    }

    [Fact]
    public async Task Oversized_arguments_are_rejected()
    {
        var (tools, b, _) = Make();
        string huge = "{\"query\":\"" + new string('a', AgentLibraryTools.MaxArgumentCharacters) + "\"}";
        Assert.StartsWith("Library tool failed:", await Call(tools, "library_search", huge));
        string longTitle = "{\"title\":\"" + new string('a', AgentLibraryTools.MaxTitleCharacters + 1) + "\"}";
        Assert.StartsWith("Library tool failed:", await Call(tools, "library_get", longTitle));
        string tags = "{\"title\":\"Hades\",\"tags\":[" + string.Join(',', Enumerable.Range(0, 21).Select(i => $"\"t{i}\"")) + "]}";
        Assert.StartsWith("Library tool failed:", await Call(tools, "library_set_tags", tags));
        string url = "{\"url\":\"https://e.com/" + new string('a', 2100) + "\"}";
        Assert.StartsWith("Library tool failed:", await Call(tools, "library_enqueue_download", url));
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task Every_call_is_audited_without_paths_or_arguments()
    {
        var (tools, _, _) = Make();
        await Call(tools, "library_search", """{"query":"hades"}""");
        await Call(tools, "library_set_favorite", """{"title":"Hades","favorite":true}""");
        await Call(tools, "library_launch", """{"title":"Celeste"}""");
        await Call(tools, "library_get", "not json");
        var audit = tools.Audit.Snapshot();
        Assert.Equal(4, audit.Count);
        Assert.Contains(audit, e => e.Tool == "library_launch" && e.Outcome == "confirmation-required" && e.Subject == "Celeste");
        Assert.Contains(audit, e => e.Tool == "library_get" && e.Outcome == "invalid-arguments");
        Assert.All(audit, e => Assert.DoesNotContain(@":\", e.Subject ?? string.Empty));
    }

    [Theory]
    [InlineData(@"opened C:\Users\paolo\Games\a.exe now", "opened [path] now")]
    [InlineData(@"share \\nas\games\x", "share [path]")]
    [InlineData("see /home/paolo/x/y ok", "see [path] ok")]
    [InlineData("plain text", "plain text")]
    public void Redact_removes_absolute_paths(string input, string expected) =>
        Assert.Equal(expected, AgentLibraryTools.Redact(input));
}
