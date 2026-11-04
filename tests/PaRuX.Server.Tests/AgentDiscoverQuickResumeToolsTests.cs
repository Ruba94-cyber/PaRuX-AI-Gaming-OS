using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AgentDiscoverQuickResumeToolsTests
{
    private static readonly DateTime EdenStart = new(2026, 11, 4, 8, 30, 0, DateTimeKind.Utc);

    private class FakeBackend : IAgentLibraryBackend
    {
        public bool GamingActive { get; set; }
        public string? Blocked;
        public string? SearchFailure;
        public List<string> Calls = new();
        public List<GameCatalogEntryDto> Store = new()
        {
            new("steam", "1145360", "Hades", new[] { "Action", "Indie" }, new[] { "Roguelike" }, "$24.99", false, null,
                @"C:\evil\page", "Hades blurb"),
            new("steam", "../x", "Odd\nTitle C:\\Users\\paolo\\secret.txt", Array.Empty<string>(), Array.Empty<string>(), null, true, null, "x"),
        };
        public List<QuickResumeAppDto> Apps = new()
        {
            new(4242, "eden", @"Eden - C:\Users\paolo\Games\mk8.nsp", EdenStart, false, true, null),
            new(4300, "notepad", "Notes", EdenStart.AddHours(1), true, true, null),
            new(900, "explorer", "Files", EdenStart, false, false, "Windows and security components cannot be paused."),
        };

        public IReadOnlyList<GameTitleDto> Titles() => Array.Empty<GameTitleDto>();
        public bool SetFavorite(string k, bool f) => true;
        public bool SetTags(string k, IReadOnlyList<string> t) => true;
        public bool SetCollections(string k, IReadOnlyList<string> c) => true;
        public Task<ManualGameLaunchResult> LaunchAsync(GameInstallationDto i, CancellationToken ct) => throw new NotSupportedException();
        public bool RemoveManualEntry(string id) => true;
        public string EnqueueDownload(string url, string? f, string? s) => "dl";

        public string? DiscoverBlockedMessage() => Blocked;
        public Task<GameCatalogListDto> DiscoverSearchAsync(string query, CancellationToken ct)
        {
            Calls.Add("search:" + query);
            return Task.FromResult(SearchFailure != null
                ? new GameCatalogListDto(GameDiscoverStatus.Failed, SearchFailure, Array.Empty<GameCatalogEntryDto>())
                : new GameCatalogListDto(GameDiscoverStatus.Ok, null, Store));
        }
        public Task<GameDiscoverResultDto> DiscoverRecommendAsync(CancellationToken ct)
        {
            Calls.Add("recommend");
            return Task.FromResult(new GameDiscoverResultDto(GameDiscoverStatus.Ok, null,
                new[] { new GameRecommendationDto(Store[0], 0.9, new[] { "Celeste" }, new[] { "Indie" }, "Because you like Celeste") }));
        }
        public Task<QuickResumeStatusDto> QuickResumeListAsync(CancellationToken ct) => Task.FromResult(new QuickResumeStatusDto(Apps));
        public Task<QuickResumeActionResult> QuickResumeSuspendAsync(QuickResumeActionRequest r, CancellationToken ct)
        { Calls.Add($"pause:{r.ProcessId}"); return Task.FromResult(new QuickResumeActionResult(true, "eden is paused.")); }
        public Task<QuickResumeActionResult> QuickResumeResumeAsync(QuickResumeActionRequest r, CancellationToken ct)
        { Calls.Add($"resume:{r.ProcessId}"); return Task.FromResult(new QuickResumeActionResult(true, "notepad is running again.")); }
    }

    private static (AgentLibraryTools Tools, FakeBackend Backend, AgentLibraryConsentStore Consent) Make()
    {
        var backend = new FakeBackend();
        var consent = new AgentLibraryConsentStore();
        return (new AgentLibraryTools(backend, consent), backend, consent);
    }

    private static Task<string> Call(AgentLibraryTools t, string tool, string json, bool bypass = false) =>
        t.CallAsync(tool, json, CancellationToken.None, null, bypass);

    private static string IdFrom(string text)
    {
        int at = text.IndexOf("confirmation_id \"", StringComparison.Ordinal) + "confirmation_id \"".Length;
        return text[at..text.IndexOf('"', at)];
    }

    private static string Started(DateTime d) => d.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    private static string PauseJson() => "{\"process_id\":4242,\"started_utc\":\"" + Started(EdenStart) + "\"}";
    private static string ResumeJson() => "{\"process_id\":4300,\"started_utc\":\"" + Started(EdenStart.AddHours(1)) + "\"}";
    private static string WithId(string json, string id) => json.TrimEnd('}') + ",\"confirmation_id\":\"" + id + "\"}";

    [Fact]
    public void New_tools_are_registered_with_closed_bounded_schemas_and_correct_permission_classes()
    {
        var (tools, _, _) = Make();
        var byName = tools.GetToolSchemas().ToDictionary(s => s!["function"]!["name"]!.GetValue<string>(), s => s!["function"]!["parameters"]!);
        foreach (string n in new[] { "discover_search", "discover_recommend", "quick_resume_list", "quick_resume_pause", "quick_resume_resume" })
        {
            Assert.True(byName.ContainsKey(n));
            Assert.True(AgentLibraryTools.IsBuiltInTool(n));
            Assert.False(byName[n]["additionalProperties"]!.GetValue<bool>());
        }
        Assert.Equal(80, byName["discover_search"]["properties"]!["query"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(25, byName["discover_search"]["properties"]!["limit"]!["maximum"]!.GetValue<int>());
        Assert.Equal(10, byName["discover_recommend"]["properties"]!["limit"]!["maximum"]!.GetValue<int>());
        Assert.Equal(1, byName["quick_resume_pause"]["properties"]!["process_id"]!["minimum"]!.GetValue<int>());
        Assert.Contains("process_id", byName["quick_resume_resume"]["required"]!.AsArray().Select(x => x!.GetValue<string>()));

        Assert.All(new[] { "discover_search", "discover_recommend", "quick_resume_list" }, n => Assert.False(AgentLibraryTools.RequiresConfirmation(n)));
        Assert.All(new[] { "quick_resume_pause", "quick_resume_resume" }, n => Assert.True(AgentLibraryTools.RequiresConfirmation(n)));
    }

    [Fact]
    public async Task Discover_search_lists_titles_with_price_and_store_page_and_never_leaks_paths_or_untrusted_urls()
    {
        var (tools, b, _) = Make();
        string r = await Call(tools, "discover_search", """{"query":"hades"}""");
        Assert.Contains("Hades [store: steam, id: 1145360] price $24.99", r);
        Assert.Contains("https://store.steampowered.com/app/1145360/", r);
        Assert.DoesNotContain(@"C:\", r);
        Assert.DoesNotContain("evil", r);          // store-supplied URL is rebuilt from the validated id
        Assert.DoesNotContain("Odd\nTitle", r);    // control characters in store text are flattened
        Assert.Contains("free to play", r);
        Assert.Contains("Nothing was downloaded", r);
        Assert.Contains("search:hades", b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == "discover_search" && e.Outcome == "success");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"a","limit":26}""")]
    [InlineData("""{"query":"a","extra":1}""")]
    public async Task Discover_search_rejects_invalid_arguments(string json)
    {
        var (tools, b, _) = Make();
        Assert.StartsWith("Library tool failed:", await Call(tools, "discover_search", json));
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task Discover_search_rejects_a_query_over_the_bound()
    {
        var (tools, b, _) = Make();
        string r = await Call(tools, "discover_search", "{\"query\":\"" + new string('a', 81) + "\"}");
        Assert.StartsWith("Library tool failed:", r);
        Assert.Empty(b.Calls);
    }

    [Theory]
    [InlineData("discover_search", """{"query":"hades"}""")]
    [InlineData("discover_recommend", "{}")]
    public async Task Discover_is_refused_with_a_clear_message_when_disabled_and_contacts_nothing(string tool, string json)
    {
        var (tools, b, _) = Make();
        b.Blocked = "Game Discover is off. Turn it on in Gaming settings to contact the Steam store.";
        string r = await Call(tools, tool, json);
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("Game Discover is off", r);
        Assert.Empty(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "discover-disabled");
    }

    [Theory]
    [InlineData("discover_search", """{"query":"hades"}""")]
    [InlineData("discover_recommend", "{}")]
    public async Task Discover_is_refused_while_gaming_hard_off_is_active(string tool, string json)
    {
        var (tools, b, _) = Make();
        b.GamingActive = true;
        string r = await Call(tools, tool, json);
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("Gaming Mode", r);
        Assert.Empty(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "gaming-hard-off");
    }

    [Fact]
    public async Task Discover_recommend_returns_store_titles_with_reason_and_no_download()
    {
        var (tools, _, consent) = Make();
        string r = await Call(tools, "discover_recommend", "{}");
        Assert.Contains("Hades", r);
        Assert.Contains("because you play: Celeste", r);
        Assert.Contains("Nothing was downloaded", r);
        Assert.Empty(consent.List());
    }

    [Fact]
    public async Task Discover_surfaces_a_provider_failure_status_honestly()
    {
        var (tools, b, _) = Make();
        b.SearchFailure = "Discover could not reach the store.";
        string r = await Call(tools, "discover_search", """{"query":"x"}""");
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("could not reach", r);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == "discover_search" && e.Outcome == "failed");
    }

    [Fact]
    public async Task Quick_resume_list_shows_identity_and_state_without_absolute_paths()
    {
        var (tools, _, _) = Make();
        string r = await Call(tools, "quick_resume_list", "{}");
        Assert.Contains("eden process_id 4242 started_utc " + Started(EdenStart), r);
        Assert.Contains("PAUSED", r);
        Assert.Contains("cannot be paused", r);
        Assert.DoesNotContain(@"C:\", r);
        Assert.Contains("[path]", r);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == "quick_resume_list" && e.Outcome == "success");
    }

    [Theory]
    [InlineData("quick_resume_pause")]
    [InlineData("quick_resume_resume")]
    public async Task Pause_and_resume_need_a_single_use_confirmation_even_in_bypass_mode(string tool)
    {
        var (tools, b, consent) = Make();
        string json = tool == "quick_resume_pause" ? PauseJson() : ResumeJson();

        string first = await Call(tools, tool, json, bypass: true);
        Assert.Contains("Confirmation required", first);
        Assert.Empty(b.Calls);
        Assert.Contains("Confirmation required", await Call(tools, tool, WithId(json, "deadbeef"), bypass: true));
        Assert.Empty(b.Calls);

        string id = IdFrom(first);
        Assert.Contains("Confirmation required", await Call(tools, tool, WithId(json, id)));  // pending, not yet approved
        Assert.Empty(b.Calls);
        Assert.True(consent.Approve(id));
        string done = await Call(tools, tool, WithId(json, id));
        Assert.StartsWith("OK:", done);
        Assert.Single(b.Calls);
        // Spent: replaying the same id does nothing.
        Assert.Contains("Confirmation required", await Call(tools, tool, WithId(json, id)));
        Assert.Single(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "confirmation-required");
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "success");
    }

    [Fact]
    public async Task A_confirmation_for_one_process_cannot_pause_another()
    {
        var (tools, b, consent) = Make();
        string id = IdFrom(await Call(tools, "quick_resume_pause", PauseJson()));
        consent.Approve(id);
        b.Apps.Add(new QuickResumeAppDto(5000, "other", "Other", EdenStart, false, true, null));
        string other = "{\"process_id\":5000,\"started_utc\":\"" + Started(EdenStart) + "\",\"confirmation_id\":\"" + id + "\"}";
        Assert.Contains("Confirmation required", await Call(tools, "quick_resume_pause", other));
        Assert.Empty(b.Calls);
    }

    [Fact]
    public async Task Protected_or_unpausable_processes_are_refused_before_any_confirmation_is_requested()
    {
        var (tools, b, consent) = Make();
        string r = await Call(tools, "quick_resume_pause", "{\"process_id\":900,\"started_utc\":\"" + Started(EdenStart) + "\"}");
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("cannot be paused", r);
        // Even if the list wrongly said CanSuspend, the policy still refuses a protected name.
        b.Apps.Add(new QuickResumeAppDto(901, "lsass", "x", EdenStart, false, true, null));
        string p = await Call(tools, "quick_resume_pause", "{\"process_id\":901,\"started_utc\":\"" + Started(EdenStart) + "\"}");
        Assert.StartsWith("Library tool failed:", p);
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == "quick_resume_pause" && e.Outcome == "refused" && e.Subject == "explorer");
    }

    [Fact]
    public async Task Stale_identity_unknown_process_and_resume_of_an_unpaused_app_are_refused()
    {
        var (tools, b, consent) = Make();
        // Same pid, different start time: the pid was reused.
        string stale = await Call(tools, "quick_resume_pause", "{\"process_id\":4242,\"started_utc\":\"" + Started(EdenStart.AddMinutes(10)) + "\"}");
        Assert.StartsWith("Library tool failed:", stale);
        Assert.Contains("quick_resume_list", stale);
        // Resume of something PaRuX did not pause.
        Assert.Contains("did not pause", await Call(tools, "quick_resume_resume", PauseJson()));
        Assert.StartsWith("Library tool failed:", await Call(tools, "quick_resume_pause", """{"process_id":4242,"started_utc":"yesterday"}"""));
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
    }

    [Theory]
    [InlineData("""{"process_id":0,"started_utc":"2026-11-04T08:30:00Z"}""")]
    [InlineData("""{"process_id":"4242","started_utc":"2026-11-04T08:30:00Z"}""")]
    [InlineData("""{"started_utc":"2026-11-04T08:30:00Z"}""")]
    [InlineData("""{"process_id":4242}""")]
    [InlineData("""{"process_id":4242,"started_utc":"2026-11-04T08:30:00Z","path":"C:\\x"}""")]
    public async Task Pause_validates_its_arguments(string json)
    {
        var (tools, b, consent) = Make();
        Assert.StartsWith("Library tool failed:", await Call(tools, "quick_resume_pause", json));
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
    }

    [Theory]
    [InlineData("quick_resume_pause")]
    [InlineData("quick_resume_resume")]
    public async Task Pause_and_resume_are_refused_under_gaming_hard_off(string tool)
    {
        var (tools, b, consent) = Make();
        b.GamingActive = true;
        string r = await Call(tools, tool, tool == "quick_resume_pause" ? PauseJson() : ResumeJson());
        Assert.StartsWith("Library tool failed:", r);
        Assert.Contains("Gaming Mode", r);
        Assert.Empty(consent.List());
        Assert.Empty(b.Calls);
        Assert.Contains(tools.Audit.Snapshot(), e => e.Tool == tool && e.Outcome == "gaming-hard-off");
    }

    [Fact]
    public async Task Audit_for_the_new_tools_holds_names_and_outcomes_only()
    {
        var (tools, _, _) = Make();
        await Call(tools, "quick_resume_list", "{}");
        await Call(tools, "quick_resume_pause", PauseJson());
        await Call(tools, "discover_search", """{"query":"hades"}""");
        var audit = tools.Audit.Snapshot();
        Assert.Equal(3, audit.Count);
        Assert.Contains(audit, e => e.Tool == "quick_resume_pause" && e.Subject == "eden");
        Assert.All(audit, e => { Assert.DoesNotContain(@":\", e.Subject ?? string.Empty); Assert.DoesNotContain("4242", e.Subject ?? string.Empty); });
    }

    [Fact]
    public async Task Skill_allow_list_blocks_the_new_tools()
    {
        var (tools, b, _) = Make();
        var allowed = new[] { "library_search" };
        Assert.Empty(tools.GetToolSchemas(allowed).Where(s => s!["function"]!["name"]!.GetValue<string>().StartsWith("quick_", StringComparison.Ordinal)));
        string r = await tools.CallAsync("quick_resume_pause", PauseJson(), CancellationToken.None, allowed);
        Assert.StartsWith("Library tool failed:", r);
        Assert.Empty(b.Calls);
    }
}
