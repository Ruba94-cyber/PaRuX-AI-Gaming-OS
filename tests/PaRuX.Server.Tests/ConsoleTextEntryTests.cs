using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConsoleTextEntryTests
{
    [Fact]
    public void Insert_places_text_at_the_caret_and_advances_it()
    {
        Assert.Equal(("abXc", 3), ConsoleTextEntry.Insert("abc", 2, "X"));
        Assert.Equal(("a", 1), ConsoleTextEntry.Insert(null, 0, "a"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("<")]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("/")]
    public void Control_markup_and_multi_character_keys_are_rejected(string key) =>
        Assert.Equal(("abc", 1), ConsoleTextEntry.Insert("abc", 1, key));

    [Fact]
    public void Length_limit_is_enforced()
    {
        string full = new('a', ConsoleTextEntry.MaxLength);
        Assert.Equal((full, 5), ConsoleTextEntry.Insert(full, 5, "b"));
    }

    [Fact]
    public void Caret_is_clamped_into_the_text()
    {
        Assert.Equal(("abcd", 4), ConsoleTextEntry.Insert("abc", 99, "d"));
        Assert.Equal(("dabc", 1), ConsoleTextEntry.Insert("abc", -5, "d"));
    }

    [Fact]
    public void Backspace_removes_the_previous_character_and_is_safe_at_the_start()
    {
        Assert.Equal(("ac", 1), ConsoleTextEntry.Backspace("abc", 2));
        Assert.Equal(("abc", 0), ConsoleTextEntry.Backspace("abc", 0));
        Assert.Equal((string.Empty, 0), ConsoleTextEntry.Backspace(null, 3));
    }

    [Fact]
    public void Clear_empties_the_field() =>
        Assert.Equal((string.Empty, 0), ConsoleTextEntry.Clear("abc", 2));

    [Fact]
    public void Every_key_on_the_keyboard_is_accepted()
    {
        foreach (string row in ConsoleTextEntry.Rows)
            foreach (char key in row)
                Assert.True(ConsoleTextEntry.IsAllowedKey(key.ToString()), key.ToString());
        Assert.True(ConsoleTextEntry.IsAllowedKey(" "));
    }
}
