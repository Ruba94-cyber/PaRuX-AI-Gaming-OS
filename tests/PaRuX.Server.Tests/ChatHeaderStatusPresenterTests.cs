using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatHeaderStatusPresenterTests
{
    private static LocalAiState State(bool healthy = false, bool process = false, bool game = false, string profile = "")
        => new() { Healthy = healthy, ServerProcessPresent = process, GameActive = game, ActiveProfile = profile };

    [Fact]
    public void Idle_without_any_work_still_says_model_idle()
    {
        var header = ChatHeaderStatusPresenter.Describe(State(), null);
        Assert.Equal("Model idle", header.Text);
        Assert.Equal(ChatHeaderDot.Off, header.Dot);
    }

    [Fact]
    public void Running_coding_task_with_no_model_yet_names_the_task_stage_not_idle()
    {
        var header = ChatHeaderStatusPresenter.Describe(State(), "Indexing repository context");
        Assert.Equal("Coding task · Indexing repository context", header.Text);
        Assert.Equal(ChatHeaderDot.Warn, header.Dot);
    }

    [Fact]
    public void Model_process_starting_says_loading_even_while_a_coding_task_runs()
    {
        var header = ChatHeaderStatusPresenter.Describe(State(process: true), "Indexing repository context");
        Assert.Equal("Loading model… · coding task", header.Text);
        Assert.Equal(ChatHeaderDot.Warn, header.Dot);
    }

    [Fact]
    public void Healthy_model_shows_profile_and_marks_active_coding_work()
    {
        var header = ChatHeaderStatusPresenter.Describe(State(healthy: true, process: true, profile: "Qwen3.5-4B Standard GPU"), "Generating");
        Assert.Equal("Qwen3.5-4B · Standard · coding task", header.Text);
        Assert.Equal(ChatHeaderDot.Ok, header.Dot);
        Assert.Equal("Qwen3.5-4B · Standard", ChatHeaderStatusPresenter.Describe(State(healthy: true, profile: "Qwen3.5-4B Standard GPU"), null).Text);
    }

    [Fact]
    public void Gaming_pause_wins_over_everything()
    {
        var header = ChatHeaderStatusPresenter.Describe(State(game: true, process: true), "Generating");
        Assert.Equal("Paused for gaming", header.Text);
        Assert.Equal(ChatHeaderDot.Warn, header.Dot);
    }

    [Theory]
    [InlineData("Role: · Standard", "Ruolo: · Standard")]
    [InlineData("Model idle", "Modello inattivo")]
    [InlineData("Paused for gaming", "In pausa per il gioco")]
    [InlineData("Loading model… · coding task", "Caricamento del modello… · attività di codice")]
    public void Italian_header_has_no_english_phrase(string english, string italian)
    {
        Assert.Equal(italian, ChatHeaderStatusPresenter.ToItalian(english));
    }

    [Theory]
    [InlineData("Ready · Start chatting in Chat", "Pronto · Inizia a conversare in Chat")]
    [InlineData("Needs attention · Check runtime status in Settings", "Richiede attenzione · Controlla lo stato del runtime in Impostazioni")]
    [InlineData("Starting · Loading local engine…", "Avvio · Caricamento del motore locale…")]
    public void Italian_runtime_health_line_has_no_english_phrase(string english, string italian)
    {
        Assert.Equal(italian, ComponentLifecyclePresenter.BadgeToItalian(english));
    }

    [Theory]
    [InlineData("Normal", "Normale")]
    [InlineData("Fast", "Veloce")]
    [InlineData("Deep reasoning", "Ragionamento approfondito")]
    public void Italian_route_badge_has_no_english_label(string english, string italian)
    {
        Assert.Equal(italian, RouterService.LabelToItalian(english));
    }

    [Fact]
    public void Italian_answering_model_caption_is_italian()
    {
        Assert.Equal("Modello: Qwen3-4B-Q4_K_M (Manuale)", AnsweringModelLabelFormatter.FormatItalian("Qwen3-4B-Q4_K_M.gguf", true));
        Assert.Equal("Modello: Qwen3-4B-Q4_K_M (Auto)", AnsweringModelLabelFormatter.FormatItalian("Qwen3-4B-Q4_K_M.gguf", false));
        Assert.Null(AnsweringModelLabelFormatter.FormatItalian(null, true));
    }
}
