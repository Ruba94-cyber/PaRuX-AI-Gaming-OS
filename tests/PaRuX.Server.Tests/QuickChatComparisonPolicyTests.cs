using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuickChatComparisonPolicyTests
{
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
    public void Hardware_suggestions_are_conservative_estimates()
    {
        var image = MediaHardwarePresetPolicy.Suggest("image", 8L * 1024 * 1024 * 1024, false);
        var video = MediaHardwarePresetPolicy.Suggest("video", 8L * 1024 * 1024 * 1024, false);
        Assert.Equal(512, image.Width);
        Assert.True(image.RequiresConfirmation);
        Assert.Equal(25, video.Frames);
        Assert.True(video.RequiresConfirmation);
    }
}
