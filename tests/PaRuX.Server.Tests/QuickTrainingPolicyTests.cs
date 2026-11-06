using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuickTrainingPolicyTests
{
    [Theory]
    [InlineData(true, false, "sft")]
    [InlineData(true, true, "sft")]
    [InlineData(false, true, "dpo")]
    [InlineData(false, false, null)]
    public void Quick_mode_only_selects_real_supported_text_dataset_shapes(
        bool supervised, bool preference, string? expected) =>
        Assert.Equal(expected, QuickTrainingPolicy.ChooseMethod(supervised, preference));

    [Fact]
    public void Quick_mode_bounds_long_high_rank_jobs_without_erasing_valid_learning_rate()
    {
        var settings = QuickTrainingPolicy.Create("sft", 64, 5000, 1e-4, 8192);
        Assert.Equal(16, settings.LoraRank);
        Assert.Equal(300, settings.MaxSteps);
        Assert.Equal(2048, settings.MaxSequenceLength);
        Assert.Equal(1e-4, settings.LearningRate);
    }

    [Fact]
    public void Short_runs_keep_the_recommended_settings_within_the_fast_budget()
    {
        var settings = QuickTrainingPolicy.Create("dpo", 8, 80, 2e-6, 1024);
        Assert.Equal("dpo", settings.Method);
        Assert.Equal(8, settings.LoraRank);
        Assert.Equal(80, settings.MaxSteps);
        Assert.Equal(2e-6, settings.LearningRate);
        Assert.Equal(1024, settings.MaxSequenceLength);
    }

    [Fact]
    public void Invalid_recommendations_receive_finite_nonzero_defaults()
    {
        var settings = QuickTrainingPolicy.Create("dpo", 0, 0, double.NaN, 0);
        Assert.Equal(16, settings.LoraRank);
        Assert.Equal(60, settings.MaxSteps);
        Assert.Equal(5e-6, settings.LearningRate);
        Assert.Equal(2048, settings.MaxSequenceLength);
    }

    [Theory]
    [InlineData(false, 20, 0, false)]
    [InlineData(true, 0, 0, false)]
    [InlineData(true, 20, 2, false)]
    [InlineData(true, 20, 0, true)]
    public void Quick_mode_does_not_skip_preflight_blockers_or_invalid_rows(
        bool canStart, int examples, int invalidLines, bool truncated) =>
        Assert.NotNull(QuickTrainingPolicy.Refusal(canStart, examples, invalidLines, truncated));

    [Fact]
    public void Clean_complete_checked_dataset_passes_quick_gate() =>
        Assert.Null(QuickTrainingPolicy.Refusal(true, 150, 0, false));

    [Fact]
    public void Unsupported_multimodal_methods_require_advanced_training() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuickTrainingPolicy.Create("vision-sft", 8, 60, 1e-4, 2048));
}
