using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelHubStartPresenterTests
{
    private static StartEngineInput Engine(bool ready = false, string state = "idle", long pending = 3L * 1024 * 1024 * 1024,
        double progress = 0, bool indeterminate = false, string? error = null, bool known = true) =>
        new("Image engine (ComfyUI)", known, ready, state, pending, progress, indeterminate, error);

    private static StartModelInput Model(string? active = null, string? offer = "Z-Image Turbo", long bytes = 7L * 1024 * 1024 * 1024,
        string? fit = "runs well on your GPU", bool downloadable = true, bool downloading = false, double progress = 0,
        bool downloaded = false, bool known = true) =>
        new(known, active, offer, bytes, fit, downloadable, downloading, progress, downloaded);

    private static StartCard Card(StartEngineInput? engine, StartModelInput? model, string id = "image", bool it = false) =>
        ModelHubStartPresenter.Build(new[] { new StartCapabilityInput(id, engine, model) }, it).Single();

    [Fact]
    public void Everything_in_place_is_ready_and_offers_to_change_the_model()
    {
        StartCard card = Card(Engine(ready: true), Model(active: "z_image_turbo.safetensors"));

        Assert.Equal(StartReadiness.Ready, card.Readiness);
        Assert.Equal("Ready", card.StatusText);
        Assert.Equal(StartActionKind.None, card.Primary.Kind);
        Assert.Equal(StartActionKind.OpenModels, card.Secondary!.Kind);
        Assert.All(card.Steps, s => Assert.Equal(StartStepState.Done, s.State));
        Assert.Equal("z_image_turbo.safetensors", card.Steps.Last().Detail);
    }

    [Fact]
    public void Missing_engine_and_model_puts_the_engine_first_and_counts_two_steps()
    {
        StartCard card = Card(Engine(), Model());

        Assert.Equal(StartReadiness.NeedsSetup, card.Readiness);
        Assert.Equal("2 steps left", card.StatusText);
        Assert.Equal(StartActionKind.InstallEngine, card.Primary.Kind);
        Assert.Equal("Install the engine (3.0 GB)", card.Primary.Label);
        Assert.Contains("Not installed", card.Steps[0].Detail);
        Assert.Equal("Suggested: Z-Image Turbo · 7.0 GB · runs well on your GPU", card.Steps[1].Detail);
    }

    [Fact]
    public void Engine_installed_leaves_one_step_and_offers_the_suggested_download()
    {
        StartCard card = Card(Engine(ready: true), Model());

        Assert.Equal("1 step left", card.StatusText);
        Assert.Equal(StartActionKind.DownloadModel, card.Primary.Kind);
        Assert.Equal("Download the model (7.0 GB)", card.Primary.Label);
        Assert.True(card.Primary.Enabled);
    }

    [Fact]
    public void Running_engine_install_shows_progress_and_a_cancel_action()
    {
        StartCard card = Card(Engine(state: "running", progress: 0.43), Model());

        Assert.Equal(StartReadiness.Installing, card.Readiness);
        Assert.Equal("Installing 43%", card.StatusText);
        Assert.Equal(0.43, card.Progress);
        Assert.Equal(StartActionKind.CancelEngine, card.Primary.Kind);
    }

    [Fact]
    public void Indeterminate_install_has_no_fake_percentage()
    {
        StartCard card = Card(Engine(state: "running", indeterminate: true), Model());

        Assert.Equal("Installing…", card.StatusText);
        Assert.True(card.Indeterminate);
    }

    [Fact]
    public void Running_model_download_reports_downloading_and_disables_the_button()
    {
        StartCard card = Card(Engine(ready: true), Model(downloading: true, progress: 0.5));

        Assert.Equal(StartReadiness.Installing, card.Readiness);
        Assert.Equal("Downloading 50%", card.StatusText);
        Assert.False(card.Primary.Enabled);
        Assert.Equal(StartActionKind.None, card.Primary.Kind);
    }

    [Fact]
    public void Failed_engine_setup_is_a_problem_with_a_retry_that_names_the_error()
    {
        StartCard card = Card(Engine(state: "failed", error: "Disk is full"), Model());

        Assert.Equal(StartReadiness.Problem, card.Readiness);
        Assert.Equal("Try again", card.Primary.Label);
        Assert.Equal(StartActionKind.InstallEngine, card.Primary.Kind);
        Assert.Equal("Disk is full", card.Steps[0].Detail);
    }

    [Fact]
    public void Blocked_engine_is_reported_honestly_with_no_action()
    {
        StartCard card = Card(Engine(state: "blocked"), Model());

        Assert.Equal(StartReadiness.Problem, card.Readiness);
        Assert.Equal("Not available on this PC", card.StatusText);
        Assert.Equal(StartActionKind.None, card.Primary.Kind);
        Assert.False(card.Primary.Enabled);
    }

    [Fact]
    public void Downloaded_but_not_yet_installed_model_asks_to_finish()
    {
        StartCard card = Card(Engine(ready: true), Model(downloaded: true));

        Assert.Equal(StartActionKind.UseDownloadedModel, card.Primary.Kind);
        Assert.Contains("just needs activating", card.Steps[1].Detail);
    }

    [Fact]
    public void No_downloadable_offer_falls_back_to_browsing()
    {
        StartCard card = Card(Engine(ready: true), Model(offer: null, downloadable: false));

        Assert.Equal(StartActionKind.OpenModels, card.Primary.Kind);
        Assert.Equal("No suitable model found yet", card.Steps[1].Detail);
    }

    [Fact]
    public void Offer_that_cannot_be_downloaded_is_not_offered_for_download()
    {
        StartCard card = Card(Engine(ready: true), Model(downloadable: false));

        Assert.Equal(StartActionKind.OpenModels, card.Primary.Kind);
    }

    [Fact]
    public void Unknown_state_is_checking_and_offers_nothing()
    {
        StartCard card = Card(Engine(known: false), Model(known: false));

        Assert.Equal(StartReadiness.Checking, card.Readiness);
        Assert.Equal("Checking…", card.StatusText);
        Assert.Equal(StartActionKind.None, card.Primary.Kind);
        Assert.False(card.Primary.Enabled);
    }

    [Fact]
    public void Chat_without_a_runtime_points_to_settings_not_to_a_hub_install()
    {
        var runtime = new StartEngineInput("Chat engine", true, false, "idle", 0, 0, false, null);
        StartCard card = Card(runtime, Model(offer: "Qwen3 4B"), id: "chat");

        Assert.Equal(StartActionKind.OpenRuntimeSettings, card.Primary.Kind);
        Assert.Equal("Not set up yet", card.Steps[0].Detail);
    }

    [Fact]
    public void Engine_only_capability_is_ready_when_the_engine_is()
    {
        var engine = new StartEngineInput("Image-to-3D engine", true, true, "completed", 0, 1, false, null);
        StartCard card = Card(engine, null, id: "3d");

        Assert.Equal(StartReadiness.Ready, card.Readiness);
        Assert.Single(card.Steps);
        Assert.Null(card.Secondary);
    }

    [Fact]
    public void Italian_wording_is_used_when_requested()
    {
        StartCard card = Card(Engine(), Model(), it: true);

        Assert.Equal("Immagini", card.Title);
        Assert.Equal("Mancano 2 passaggi", card.StatusText);
        Assert.Equal("Installa il motore (3.0 GB)", card.Primary.Label);
        Assert.StartsWith("Consigliato: ", card.Steps[1].Detail);
    }

    [Fact]
    public void Summary_counts_ready_capabilities_in_both_languages()
    {
        StartCard[] cards =
        {
            Card(Engine(ready: true), Model(active: "x")),
            Card(Engine(), Model()),
            Card(Engine(ready: true), Model(active: "y"))
        };

        Assert.Equal("2 of 3 abilities ready", ModelHubStartPresenter.Summarize(cards, false).Line);
        Assert.Equal("2 su 3 funzioni pronte", ModelHubStartPresenter.Summarize(cards, true).Line);
    }

    [Theory]
    [InlineData("fits", "runs well on your GPU")]
    [InlineData("offload", "works, but slower")]
    [InlineData("too_large", "too large for this PC")]
    [InlineData("unknown", "")]
    [InlineData(null, "")]
    public void Fit_classes_are_put_in_plain_words(string? fitClass, string expected) =>
        Assert.Equal(expected, ModelHubStartPresenter.FitWords(fitClass, false));

    [Fact]
    public void Capability_order_matches_the_hub_engines_and_has_metadata_for_each()
    {
        foreach (string id in ModelHubStartPresenter.CapabilityOrder)
        {
            StartCard card = Card(null, null, id: id);
            Assert.False(string.IsNullOrWhiteSpace(card.Tagline), id);
            Assert.NotEqual(id, card.Glyph);
        }
    }

    [Theory]
    [InlineData(0L, "")]
    [InlineData(500L * 1024 * 1024, "500 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, "3.5 GB")]
    public void Sizes_are_formatted_for_people(long bytes, string expected) =>
        Assert.Equal(expected, ModelHubStartPresenter.FormatSize(bytes));
}
