using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatSamplingPreferencesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-sampling-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "chat-sampling.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Values_are_clamped_to_what_Core_accepts()
    {
        var v = ChatSamplingRules.Normalize(new ChatSamplingValues(9, -3, 99_999_999, -5));
        Assert.Equal(2.0, v.Temperature);
        Assert.Equal(0.0, v.TopP);
        Assert.Equal(ChatSamplingRules.MaxTokensCeiling, v.MaxTokens);
        Assert.Null(v.Seed);
        Assert.Null(ChatSamplingRules.Normalize(new ChatSamplingValues(double.NaN, double.PositiveInfinity, null, null)).Temperature);
        Assert.Equal(1, ChatSamplingRules.Normalize(new ChatSamplingValues(null, null, 0, null)).MaxTokens);
    }

    [Fact]
    public void Settings_survive_a_reopen_and_defaults_remove_the_file()
    {
        new ChatSamplingStore(FilePath).Save(new ChatSamplingValues(0.3, 0.9, 512, 42));
        var reopened = new ChatSamplingStore(FilePath).Load();
        Assert.Equal(new ChatSamplingValues(0.3, 0.9, 512, 42), reopened);

        new ChatSamplingStore(FilePath).Save(ChatSamplingValues.Default);
        Assert.False(File.Exists(FilePath));
        Assert.True(new ChatSamplingStore(FilePath).Load().IsDefault);
    }

    [Fact]
    public void A_damaged_file_means_defaults_not_a_crash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        Assert.True(new ChatSamplingStore(FilePath).Load().IsDefault);
    }
}
