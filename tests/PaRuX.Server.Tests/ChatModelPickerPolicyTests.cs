using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatModelPickerPolicyTests
{
    private static ManagedModelInfo Model(int n, string? alias = null) => new()
    {
        FileName = "model-" + n.ToString("D3") + ".gguf",
        Alias = alias,
        Quantization = n % 2 == 0 ? "Q4_K_M" : "Q8_0",
        SizeBytes = 1024 * (n + 1)
    };

    [Fact]
    public void Quick_flyout_is_bounded_and_keeps_the_pinned_model_visible()
    {
        ManagedModelInfo[] models = Enumerable.Range(0, 125).Select(n => Model(n)).ToArray();
        var normal = ChatModelPickerPolicy.Quick(models, null);
        Assert.Equal(ChatModelPickerPolicy.QuickChoices, normal.Count);
        Assert.DoesNotContain(normal, m => m.FileName == "model-124.gguf");

        var pinned = ChatModelPickerPolicy.Quick(models, "MODEL-124.GGUF");
        Assert.Equal(ChatModelPickerPolicy.QuickChoices, pinned.Count);
        Assert.Equal("model-124.gguf", pinned[0].FileName);
        Assert.Equal(pinned.Count, pinned.Select(m => m.FileName).Distinct().Count());
        Assert.Equal(normal.Count, ChatModelPickerPolicy.Quick(models, "does-not-exist").Count);
    }

    [Fact]
    public void Low_ram_browse_can_put_known_small_files_first_without_guessing_unknown_sizes()
    {
        var models = new[]
        {
            new ManagedModelInfo { FileName = "large.gguf", SizeBytes = 8_000_000_000 },
            new ManagedModelInfo { FileName = "unknown.gguf", SizeBytes = 0 },
            new ManagedModelInfo { FileName = "tiny.gguf", SizeBytes = 900_000_000 }
        };
        var page = ChatModelPickerPolicy.Browse(
            models, null, 0, ChatModelBrowseSort.SmallestFileFirst);
        Assert.Equal(new[] { "tiny.gguf", "large.gguf", "unknown.gguf" },
            page.Visible.Select(m => m.FileName));
    }

    [Fact]
    public void Measured_speed_sort_distinguishes_observed_speed_from_unknown_not_benchmarking_automatically()
    {
        var models = new[]
        {
            new ManagedModelInfo { FileName = "slow.gguf", LastBenchmarkTokensPerSecond = 7.5 },
            new ManagedModelInfo { FileName = "unknown.gguf" },
            new ManagedModelInfo { FileName = "fast.gguf", LastBenchmarkTokensPerSecond = 35.5 },
            new ManagedModelInfo { FileName = "invalid.gguf", LastBenchmarkTokensPerSecond = -10 }
        };
        var page = ChatModelPickerPolicy.Browse(
            models, null, 0, ChatModelBrowseSort.MeasuredSpeedFirst);
        Assert.Equal(new[] { "fast.gguf", "slow.gguf", "invalid.gguf", "unknown.gguf" },
            page.Visible.Select(m => m.FileName));
        Assert.Equal(4, page.TotalMatches);
    }

    [Fact]
    public void Quick_compare_accepts_two_distinct_installed_models_from_distant_pages()
    {
        var models = Enumerable.Range(0, 125).Select(i => Model(i)).ToArray();
        Assert.True(ChatModelPickerPolicy.CanCompare(
            models, "MODEL-001.GGUF", "model-124.gguf"));
        Assert.False(ChatModelPickerPolicy.CanCompare(
            models, "model-001.gguf", "MODEL-001.GGUF"));
        Assert.False(ChatModelPickerPolicy.CanCompare(
            models, "model-001.gguf", "missing-model.gguf"));
        Assert.False(ChatModelPickerPolicy.CanCompare(
            models, null, "model-124.gguf"));
        Assert.False(ChatModelPickerPolicy.CanCompare(
            Array.Empty<ManagedModelInfo>(), "model-001.gguf", "model-124.gguf"));
    }

    [Fact]
    public void Alias_is_never_a_model_identity_for_compare_execution()
    {
        ManagedModelInfo[] models =
        {
            Model(1, "Shared alias"),
            Model(2, "Shared alias")
        };
        Assert.True(ChatModelPickerPolicy.CanCompare(
            models, "model-001.gguf", "model-002.gguf"));
        Assert.False(ChatModelPickerPolicy.CanCompare(
            models, "Shared alias", "model-002.gguf"));
    }

    [Fact]
    public void Browse_can_find_the_last_of_125_models_without_rendering_all_rows()
    {
        var models = Enumerable.Range(0, 125).Select(n => Model(n)).ToArray();
        var first = ChatModelPickerPolicy.Browse(models, null, 0);
        Assert.Equal(125, first.TotalMatches);
        Assert.Equal(ChatModelPickerPolicy.BrowsePageSize, first.Visible.Count);
        Assert.Equal(6, first.TotalPages);

        var last = ChatModelPickerPolicy.Browse(models, null, 200);
        Assert.Equal(5, last.PageIndex);
        Assert.Equal(5, last.Visible.Count);
        Assert.Equal("model-124.gguf", last.Visible[^1].FileName);

        var exact = ChatModelPickerPolicy.Browse(models, "MODEL-124", 0);
        Assert.Equal("model-124.gguf", Assert.Single(exact.Visible).FileName);
    }

    [Fact]
    public void Search_supports_alias_quantization_and_mixed_case_without_duplicates()
    {
        var models = new[]
        {
            Model(1, "Research Helper"),
            Model(2, "Quick Vision"),
            Model(2, "Duplicate alias"),
            Model(3, "Another")
        };
        Assert.Equal("model-001.gguf", Assert.Single(
            ChatModelPickerPolicy.Browse(models, "research HELPER", 0).Visible).FileName);
        Assert.Single(ChatModelPickerPolicy.Browse(models, "q4_k_m", 0).Visible);
        Assert.Empty(ChatModelPickerPolicy.Browse(models, "missing", -1).Visible);
        Assert.Equal(3, ChatModelPickerPolicy.Browse(models, null, 0).TotalMatches);
    }
}
