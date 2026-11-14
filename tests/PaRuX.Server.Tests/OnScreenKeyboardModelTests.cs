using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class OnScreenKeyboardModelTests
{
    [Fact]
    public void Type_inserts_at_caret_and_advances()
    {
        var m = new OnScreenKeyboardModel("abc", 1);
        Assert.True(m.Type("X"));
        Assert.Equal("aXbc", m.Text);
        Assert.Equal(2, m.Caret);
    }

    [Fact]
    public void Shift_is_one_shot_and_caps_is_sticky()
    {
        var m = new OnScreenKeyboardModel("", 0);
        m.ToggleShift();
        Assert.Equal("Q", m.GetRows()[1][0].Label);
        m.Type("Q");
        Assert.False(m.ShiftActive);
        Assert.Equal("q", m.GetRows()[1][0].Label);
        m.ToggleCaps();
        m.Type("A");
        m.Type("B");
        Assert.True(m.CapsLock);
        Assert.Equal("Q", m.GetRows()[1][0].Label);
        m.ToggleShift(); // caps + shift => lower
        Assert.Equal("q", m.GetRows()[1][0].Label);
    }

    [Fact]
    public void Backspace_and_caret_moves_are_clamped()
    {
        var m = new OnScreenKeyboardModel("ab", 2);
        m.MoveCaret(-9);
        Assert.Equal(0, m.Caret);
        Assert.False(m.Backspace());
        m.MoveCaret(9);
        Assert.Equal(2, m.Caret);
        Assert.True(m.Backspace());
        Assert.Equal(("a", 1), (m.Text, m.Caret));
        m.MoveCaret(-1);
        m.Type("Z");
        Assert.Equal("Za", m.Text);
    }

    [Fact]
    public void Control_characters_and_length_limit_are_rejected()
    {
        var m = new OnScreenKeyboardModel("ab", 2, maxLength: 3);
        Assert.False(m.Type("\n"));
        Assert.False(m.Type("\t"));
        Assert.True(m.Type("c"));
        Assert.False(m.Type("d"));
        Assert.Equal("abc", m.Text);
    }

    [Fact]
    public void Layers_expose_distinct_stable_ids_and_reset_shift()
    {
        var m = new OnScreenKeyboardModel("", 0);
        var letters = m.GetRows().SelectMany(r => r).Select(k => k.Id).ToList();
        m.ToggleShift();
        m.ToggleLayer();
        Assert.False(m.ShiftActive);
        var symbols = m.GetRows().SelectMany(r => r).Select(k => k.Id).ToList();
        Assert.Equal(letters.Count, letters.Distinct().Count());
        Assert.Equal(symbols.Count, symbols.Distinct().Count());
        Assert.Empty(letters.Intersect(symbols));
        Assert.Contains("@", m.GetRows().SelectMany(r => r).Select(k => k.Output));
    }

    [Fact]
    public void Every_key_output_is_typeable_and_ids_do_not_depend_on_shift()
    {
        var m = new OnScreenKeyboardModel("", 0, showAccentRow: true);
        foreach (var layer in Enum.GetValues<OnScreenKeyboardLayer>())
        {
            m.SetLayer(layer);
            var before = m.GetRows().SelectMany(r => r).Select(k => k.Id).ToList();
            m.ToggleShift();
            Assert.Equal(before, m.GetRows().SelectMany(r => r).Select(k => k.Id).ToList());
            m.ToggleShift();
            foreach (var key in m.GetRows().SelectMany(r => r))
                Assert.True(m.Type(key.Output), key.Id);
        }
    }

    [Fact]
    public void Accent_row_is_optional()
    {
        Assert.Equal(4, new OnScreenKeyboardModel("", 0).GetRows().Count);
        var it = new OnScreenKeyboardModel("", 0, showAccentRow: true).GetRows();
        Assert.Equal(5, it.Count);
        Assert.Contains("è", it[4].Select(k => k.Output));
    }

    [Fact]
    public void Password_preview_is_masked_but_text_is_real()
    {
        var m = new OnScreenKeyboardModel("", 0, isPassword: true);
        m.Type("s");
        m.Type("e");
        m.Type("c");
        Assert.Equal("sec", m.Text);
        Assert.Equal(new string(OnScreenKeyboardModel.MaskChar, 3), m.Preview);
        Assert.DoesNotContain("e", m.PreviewWithCaret);
    }

    [Fact]
    public void Initial_text_is_kept_for_cancel()
    {
        var m = new OnScreenKeyboardModel("hello", 3);
        m.Type("x");
        Assert.Equal(("hello", 3), (m.InitialText, m.InitialCaret));
    }

    [Theory]
    [InlineData(196, OnScreenKeyboardPadAction.Cancel)]
    [InlineData(27, OnScreenKeyboardPadAction.Cancel)]
    [InlineData(207, OnScreenKeyboardPadAction.Done)]
    [InlineData(197, OnScreenKeyboardPadAction.Backspace)]
    [InlineData(198, OnScreenKeyboardPadAction.Space)]
    [InlineData(200, OnScreenKeyboardPadAction.CaretLeft)]
    [InlineData(199, OnScreenKeyboardPadAction.CaretRight)]
    [InlineData(201, OnScreenKeyboardPadAction.ToggleShift)]
    [InlineData(202, OnScreenKeyboardPadAction.ToggleLayer)]
    [InlineData(195, OnScreenKeyboardPadAction.None)]
    [InlineData(13, OnScreenKeyboardPadAction.None)]
    public void Pad_buttons_map_to_actions(int vk, OnScreenKeyboardPadAction expected) =>
        Assert.Equal(expected, OnScreenKeyboardModel.ResolvePad(vk));

    [Fact]
    public void Pad_actions_edit_the_text()
    {
        var m = new OnScreenKeyboardModel("ab", 2);
        m.Apply(OnScreenKeyboardPadAction.Space);
        m.Apply(OnScreenKeyboardPadAction.CaretLeft);
        m.Apply(OnScreenKeyboardPadAction.Backspace);
        Assert.Equal(("a ", 1), (m.Text, m.Caret));
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)] // Desktop mode: never
    [InlineData(true, false, true, false, false)] // physical keyboard / pointer: never
    [InlineData(true, true, false, false, false)] // read-only
    [InlineData(true, true, true, true, false)]   // already open
    public void Auto_open_rule(bool console, bool gamepad, bool editable, bool open, bool expected) =>
        Assert.Equal(expected, OnScreenKeyboardModel.ShouldAutoOpen(console, gamepad, editable, open));

    [Theory]
    [InlineData(195, true)]
    [InlineData(218, true)]
    [InlineData(194, false)]
    [InlineData(65, false)]
    [InlineData(13, false)]
    public void Gamepad_key_range(int vk, bool expected) =>
        Assert.Equal(expected, OnScreenKeyboardModel.IsGamepadKey(vk));

    [Fact]
    public void Cancel_restores_the_text_and_caret_from_when_the_keyboard_opened()
    {
        var m = new OnScreenKeyboardModel("hello", 2);
        m.Type("X"); m.Space(); m.Backspace(); m.Backspace(); m.MoveCaret(3); m.Type("Z");
        Assert.NotEqual("hello", m.Text);
        Assert.Equal(("hello", 2), m.ResultFor(commit: false));
        Assert.Equal((m.Text, m.Caret), m.ResultFor(commit: true));
    }

    [Fact]
    public void Cancel_after_clear_still_restores_the_original()
    {
        var m = new OnScreenKeyboardModel("secret", 6, isPassword: true);
        m.Clear(); m.Type("a");
        Assert.Equal(("secret", 6), m.ResultFor(false));
    }

    [Fact]
    public void Cancel_on_an_untouched_empty_field_keeps_it_empty() =>
        Assert.Equal(("", 0), new OnScreenKeyboardModel(null, 0).ResultFor(false));

    [Theory]
    [InlineData(27)]  // Escape
    [InlineData(196)] // GamepadB
    public void B_and_Escape_resolve_to_cancel_which_restores(int vk)
    {
        Assert.Equal(OnScreenKeyboardPadAction.Cancel, OnScreenKeyboardModel.ResolvePad(vk));
        var m = new OnScreenKeyboardModel("abc", 3);
        m.Type("d");
        Assert.False(m.Apply(OnScreenKeyboardModel.ResolvePad(vk))); // Cancel is the host's job
        Assert.Equal(("abc", 3), m.ResultFor(false));
    }

    [Theory]
    [InlineData(195)] [InlineData(196)] [InlineData(203)] [InlineData(204)] [InlineData(205)] [InlineData(206)] [InlineData(218)]
    public void Gamepad_keys_arm_the_auto_open_rule(int vk) =>
        Assert.True(OnScreenKeyboardModel.ShouldAutoOpen(true, OnScreenKeyboardModel.IsGamepadKey(vk), true, false));

    [Theory]
    [InlineData(9)]   // Tab
    [InlineData(37)]  // Left arrow
    [InlineData(40)]  // Down arrow
    [InlineData(13)]  // Enter
    [InlineData(115)] // F4 (explicit open path, not auto)
    public void Physical_keyboard_keys_never_arm_the_auto_open_rule(int vk) =>
        Assert.False(OnScreenKeyboardModel.ShouldAutoOpen(true, OnScreenKeyboardModel.IsGamepadKey(vk), true, false));

    [Fact]
    public void Auto_open_is_blocked_while_open_even_after_a_gamepad_key()
    {
        bool lastPad = OnScreenKeyboardModel.IsGamepadKey(203);
        Assert.True(OnScreenKeyboardModel.ShouldAutoOpen(true, lastPad, true, false));
        Assert.False(OnScreenKeyboardModel.ShouldAutoOpen(true, lastPad, true, true));
    }

    [Fact]
    public void Launch_profile_form_parses_args_and_numbers()
    {
        Assert.Equal(new[] { "-windowed", "-w", "{width}" }, GameLaunchProfileForm.ParseArgs("  -windowed  -w {width} "));
        Assert.Empty(GameLaunchProfileForm.ParseArgs(null));
        Assert.Equal("-a -b", GameLaunchProfileForm.JoinArgs(new[] { "-a", "-b" }));
        Assert.True(GameLaunchProfileForm.TryParseOptionalInt("", out int? none)); Assert.Null(none);
        Assert.True(GameLaunchProfileForm.TryParseOptionalInt(" 1920 ", out int? n)); Assert.Equal(1920, n);
        Assert.False(GameLaunchProfileForm.TryParseOptionalInt("19x", out _));
        Assert.False(GameLaunchProfileForm.TryParseOptionalInt("-5", out _));
    }
}
