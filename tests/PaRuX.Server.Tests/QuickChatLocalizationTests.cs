using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class QuickChatLocalizationTests
{
    [Theory]
    [InlineData("it", "Chiedimi qualcosa", "Invia dalla Chat rapida")]
    [InlineData("IT", "Chiedimi qualcosa", "Invia dalla Chat rapida")]
    [InlineData("en", "Ask me something", "Send from Quick Chat")]
    [InlineData("fr", "Ask me something", "Send from Quick Chat")]
    [InlineData(null, "Ask me something", "Send from Quick Chat")]
    public void Quick_chat_localizes_actions_and_falls_back_to_English(
        string? language, string expectedEmpty, string expectedSend)
    {
        var text = QuickChatStrings.ForLanguage(language);
        Assert.Equal(expectedEmpty, text.EmptyTitle);
        Assert.Equal(expectedSend, text.SendAutomation);
        Assert.All(new[] {
            text.WindowTitle, text.OpenInMain, text.OpenHelp, text.CloseHelp,
            text.CloseAutomation, text.SendHelp, text.SendAutomation,
            text.EmptyTitle, text.EmptyDescription, text.InputPlaceholder
        }, value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void Italian_and_English_maintain_same_semantic_fields()
    {
        var italian = QuickChatStrings.ForLanguage("it");
        var english = QuickChatStrings.ForLanguage("en");
        Assert.NotEqual(english.InputPlaceholder, italian.InputPlaceholder);
        Assert.NotEqual(english.OpenInMain, italian.OpenInMain);
        Assert.NotEqual(english.WindowTitle, italian.WindowTitle);
    }
}
