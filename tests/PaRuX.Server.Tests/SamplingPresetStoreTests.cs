using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SamplingPresetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-presets-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "sampling-presets.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static SamplingPreset Precise() => new() { Name = "Precise", Temperature = 0.2, TopP = 0.9, MaxTokens = 512, Seed = 7 };

    [Fact]
    public void A_preset_is_saved_listed_replaced_and_deleted()
    {
        var store = new SamplingPresetStore(FilePath);
        var (saved, error) = store.Save(Precise());
        Assert.Null(error);

        SamplingPreset edit = Precise();
        edit.Id = saved!.Id;
        edit.Temperature = 0.4;
        Assert.Null(store.Save(edit).Error);

        SamplingPreset reloaded = Assert.Single(new SamplingPresetStore(FilePath).List());
        Assert.Equal(0.4, reloaded.Temperature);
        Assert.True(store.Delete(saved.Id));
        Assert.False(store.Delete(saved.Id));
    }

    [Theory]
    [InlineData("", 0.5, 10)]
    [InlineData("x", 3.0, 10)]
    [InlineData("x", 0.5, 0)]
    [InlineData("x", 0.5, 999999)]
    public void Invalid_presets_are_refused(string name, double temperature, int maxTokens)
    {
        var store = new SamplingPresetStore(FilePath);
        Assert.NotNull(store.Save(new SamplingPreset { Name = name, Temperature = temperature, MaxTokens = maxTokens }).Error);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Duplicate_names_are_refused_and_the_count_is_bounded()
    {
        var store = new SamplingPresetStore(FilePath);
        store.Save(Precise());
        Assert.NotNull(store.Save(new SamplingPreset { Name = "precise" }).Error);
        for (int i = 1; i < SamplingPresetStore.MaximumPresets; i++) Assert.Null(store.Save(new SamplingPreset { Name = "p" + i }).Error);
        Assert.NotNull(store.Save(new SamplingPreset { Name = "too many" }).Error);
    }

    [Fact]
    public void A_preset_fills_only_what_the_request_left_unset()
    {
        var store = new SamplingPresetStore(FilePath);
        string id = store.Save(Precise()).Saved!.Id;
        var request = new ChatCompletionRequest { Temperature = 1.1 };

        Assert.True(store.TryApply(request, id));

        Assert.Equal(1.1, request.Temperature);   // the request wins
        Assert.Equal(0.9, request.TopP);
        Assert.Equal(512, request.MaxTokens);
        Assert.Equal(7, request.Seed);
    }

    [Fact]
    public void A_request_with_its_own_token_cap_keeps_it_and_an_unknown_preset_changes_nothing()
    {
        var store = new SamplingPresetStore(FilePath);
        string id = store.Save(Precise()).Saved!.Id;
        var capped = new ChatCompletionRequest { MaxCompletionTokens = 64 };
        store.TryApply(capped, id);
        Assert.Null(capped.MaxTokens);
        Assert.Equal(64, capped.MaxCompletionTokens);

        var untouched = new ChatCompletionRequest();
        Assert.False(store.TryApply(untouched, "missing"));
        Assert.Null(untouched.Temperature);
    }

    [Fact]
    public void A_damaged_file_means_no_presets()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "[ not json");
        Assert.Empty(new SamplingPresetStore(FilePath).List());
    }
}
