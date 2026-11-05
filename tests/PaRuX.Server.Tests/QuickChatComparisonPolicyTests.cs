using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuickChatComparisonPolicyTests
{
    [Fact]
    public void Typed_Quick_compare_rejects_hidden_tasks_and_extra_models()
    {
        var invalidTasks = new PaRuX.Contracts.ArenaStartRequest
        {
            ModelFileNames = new() { "first.gguf", "second.gguf" },
            TaskIds = new() { "chat-explain" },
            CustomPrompt = "Synthetic same question",
            MaxTokens = 256
        };
        Assert.Throws<System.IO.InvalidDataException>(() => QuickChatComparisonPolicy.Create(invalidTasks));
        var invalidModels = new PaRuX.Contracts.ArenaStartRequest
        {
            ModelFileNames = new() { "first.gguf", "second.gguf", "third.gguf" },
            CustomPrompt = "Synthetic same question",
            MaxTokens = 256
        };
        Assert.Throws<System.IO.InvalidDataException>(() => QuickChatComparisonPolicy.Create(invalidModels));

        var accepted = QuickChatComparisonPolicy.Create(new PaRuX.Contracts.ArenaStartRequest
        {
            ModelFileNames = new() { "first.gguf", "second.gguf" },
            CustomPrompt = "  Same prompt  ",
            MaxTokens = 256
        });
        Assert.Equal(new[] { "first.gguf", "second.gguf" }, accepted.ModelFileNames);
        Assert.Equal("Same prompt", accepted.CustomPrompt);
        Assert.Empty(accepted.TaskIds);
    }

    [Fact]
    public void Two_model_request_uses_one_identical_custom_prompt_via_existing_Core_contract()
    {
        var request = QuickChatComparisonPolicy.Create("first.gguf", "second.gguf", "  Explain DBC bits.  ");
        Assert.Equal(new[] { "first.gguf", "second.gguf" }, request.ModelFileNames);
        Assert.Empty(request.TaskIds);
        Assert.Equal("Explain DBC bits.", request.CustomPrompt);
        Assert.Equal(256, request.MaxTokens);
    }

    [Theory]
    [InlineData("model.gguf", "MODEL.gguf", "question")]
    [InlineData("../model.gguf", "second.gguf", "question")]
    [InlineData("first\n.gguf", "second.gguf", "question")]
    [InlineData("Auto", "second.gguf", "question")]
    [InlineData("first.gguf", "second.gguf", "")]
    public void Fails_closed_for_duplicate_names_paths_auto_and_empty_prompt(
        string first, string second, string prompt)
    {
        Assert.Throws<System.IO.InvalidDataException>(() =>
            QuickChatComparisonPolicy.Create(first, second, prompt));
    }

    [Fact]
    public void Comparing_does_not_route_or_mutate_personas()
    {
        string explanation = QuickChatComparisonPolicy.AutoExplanation(true);
        Assert.Contains("locali", explanation);
        Assert.Contains("non cambia", explanation);
    }

    [Fact]
    public void Game_running_never_suggests_Ai_media_gpu_usage()
    {
        var preset = MediaHardwarePresetPolicy.Suggest("image", 24L * 1024 * 1024 * 1024, true);
        Assert.False(preset.Suggested);
        Assert.True(preset.RequiresConfirmation);
    }

    [Fact]
    public void Unknown_gpu_memory_is_not_reported_as_supported()
    {
        var preset = MediaHardwarePresetPolicy.Suggest("video", 0, false);
        Assert.False(preset.Suggested);
        Assert.Equal(0, preset.Frames);
    }

    [Fact]
    public void Very_low_free_vram_never_generates_an_optimistic_media_suggestion()
    {
        const long gib = 1024L * 1024 * 1024;
        var lowImage = MediaHardwarePresetPolicy.Suggest("image", 3L * gib, false);
        var lowVideo = MediaHardwarePresetPolicy.Suggest("video", 7L * gib, false);
        Assert.False(lowImage.Suggested);
        Assert.Equal(0, lowImage.Width);
        Assert.False(lowVideo.Suggested);
        Assert.Equal(0, lowVideo.Frames);
        Assert.True(lowImage.RequiresConfirmation);
        Assert.True(lowVideo.RequiresConfirmation);
    }

    [Fact]
    public void Hardware_suggestions_are_conservative_estimates()
    {
        var image = MediaHardwarePresetPolicy.Suggest("image", 8L * 1024 * 1024 * 1024, false);
        var video = MediaHardwarePresetPolicy.Suggest("video", 8L * 1024 * 1024 * 1024, false);
        Assert.Equal(512, image.Width);
        Assert.True(image.RequiresConfirmation);
        Assert.Equal(25, video.Frames);
        Assert.True(video.RequiresConfirmation);
    }
    [Fact]
    public void Prompt_limit_matches_the_real_Core_Arena_contract()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => QuickChatComparisonPolicy.Create(
            "first.gguf", "second.gguf", new string('x', ModelArenaService.MaxPromptChars + 1)));
        Assert.Equal(ModelArenaService.MaxPromptChars,
            QuickChatComparisonPolicy.Create("first.gguf", "second.gguf",
                new string('x', ModelArenaService.MaxPromptChars)).CustomPrompt!.Length);
    }

    [Fact]
    public void Side_by_side_presenter_shows_exact_one_prompt_and_only_real_answers()
    {
        var run = new PaRuX.Contracts.ArenaRunDto
        {
            Status = "completed",
            ModelFileNames = new() { "first.gguf", "second.gguf" },
            Prompts = new()
            {
                new PaRuX.Contracts.ArenaPromptResultDto
                {
                    TaskId = "custom",
                    Prompt = "Synthetic comparison question",
                    UserPick = "first.gguf",
                    Results = new()
                    {
                        new PaRuX.Contracts.ArenaModelResultDto
                        {
                            ModelFileName = "first.gguf", Status = "completed", Output = "First answer"
                        },
                        new PaRuX.Contracts.ArenaModelResultDto
                        {
                            ModelFileName = "second.gguf", Status = "failed", Error = "Synthetic failure"
                        }
                    }
                }
            }
        };

        var presented = QuickChatComparisonPresenter.Build(run, italian: true);
        Assert.True(presented.Ready);
        Assert.Equal("Synthetic comparison question", presented.Prompt);
        Assert.Equal("First answer", presented.Left.AnswerText);
        Assert.True(presented.Left.AnswerAvailable);
        Assert.False(presented.Right.AnswerAvailable);
        Assert.Equal(string.Empty, presented.Right.AnswerText);
        Assert.Equal("first.gguf", presented.UserPreferredModel);
    }

    [Fact]
    public void Shared_Chat_projection_contains_two_columns_and_no_invented_ranking()
    {
        var run = new PaRuX.Contracts.ArenaRunDto
        {
            Status = "completed",
            ModelFileNames = new() { "first.gguf", "second.gguf" },
            Prompts = new()
            {
                new PaRuX.Contracts.ArenaPromptResultDto
                {
                    TaskId = "custom",
                    Prompt = "Synthetic prompt",
                    Results = new()
                    {
                        new PaRuX.Contracts.ArenaModelResultDto
                        {
                            ModelFileName = "first.gguf", Status = "completed", Output = "Answer A"
                        },
                        new PaRuX.Contracts.ArenaModelResultDto
                        {
                            ModelFileName = "second.gguf", Status = "failed", Error = "Synthetic failure"
                        }
                    }
                }
            }
        };
        PaRuX.Contracts.ArenaQuickCompareViewDto result =
            QuickChatComparisonPresenter.ToDto(run, italian: false);
        Assert.True(result.Ready);
        Assert.Equal("Synthetic prompt", result.Prompt);
        Assert.Equal("Answer A", result.Left.AnswerText);
        Assert.Equal("Failed", result.Right.StatusText);
        Assert.Equal(string.Empty, result.Right.AnswerText);
        Assert.Null(result.UserPreferredModel);
    }

    [Fact]
    public void Comparison_presenter_localizes_status_without_changing_model_output()
    {
        var run = new PaRuX.Contracts.ArenaRunDto
        {
            Status = "completed",
            ModelFileNames = new() { "first.gguf", "second.gguf" },
            Prompts = new()
            {
                new PaRuX.Contracts.ArenaPromptResultDto
                {
                    TaskId = "custom",
                    Prompt = "Synthetic question",
                    Results = new()
                    {
                        new PaRuX.Contracts.ArenaModelResultDto
                        {
                            ModelFileName = "first.gguf", Status = "completed", Output = "Answer one"
                        },
                        new PaRuX.Contracts.ArenaModelResultDto
                        {
                            ModelFileName = "second.gguf", Status = "failed", Error = "Synthetic error"
                        }
                    }
                }
            }
        };
        var italian = QuickChatComparisonPresenter.ToDto(run, italian: true);
        var english = QuickChatComparisonPresenter.ToDto(run, italian: false);
        Assert.Equal("Risposta ricevuta", italian.Left.StatusText);
        Assert.Equal("Answered", english.Left.StatusText);
        Assert.Equal("Answer one", italian.Left.AnswerText);
        Assert.Equal(italian.Left.AnswerText, english.Left.AnswerText);
        Assert.Equal("Errore", italian.Right.StatusText);
        Assert.Equal("Failed", english.Right.StatusText);
    }

    [Fact]
    public void Side_by_side_presenter_does_not_invent_winner_or_missing_answers()
    {
        var run = new PaRuX.Contracts.ArenaRunDto
        {
            Status = "running",
            ModelFileNames = new() { "first.gguf", "second.gguf" },
            Prompts = new()
            {
                new PaRuX.Contracts.ArenaPromptResultDto
                {
                    TaskId = "custom",
                    Prompt = "Example",
                    UserPick = "second.gguf",
                    Results = new()
                }
            }
        };
        var present = QuickChatComparisonPresenter.Build(run, italian: false);
        Assert.True(present.Ready);
        Assert.False(present.Left.AnswerAvailable);
        Assert.False(present.Right.AnswerAvailable);
        Assert.Null(present.UserPreferredModel);
        Assert.Contains("running", present.StatusText, System.StringComparison.OrdinalIgnoreCase);
        Assert.False(QuickChatComparisonPresenter.Build(null, italian: false).Ready);
    }
}
