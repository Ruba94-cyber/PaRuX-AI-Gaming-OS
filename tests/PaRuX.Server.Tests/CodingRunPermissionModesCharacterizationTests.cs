using System;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingRunPermissionModesCharacterizationTests
{
    [Theory]
    [InlineData("bypass")]
    [InlineData("BYPASS")]
    [InlineData("Bypass")]
    [InlineData("ask")]
    [InlineData("ASK")]
    [InlineData("plan")]
    [InlineData("PLAN")]
    public void IsKnown_accepts_every_spelling_of_each_mode(string value)
    {
        Assert.True(CodingRunPermissionModes.IsKnown(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("yolo")]
    [InlineData("approve")]
    [InlineData("bypass ")]
    public void IsKnown_rejects_unknown_or_blank_input(string? value)
    {
        Assert.False(CodingRunPermissionModes.IsKnown(value));
    }

    [Theory]
    [InlineData("  bypass  ", "bypass")]
    [InlineData("Ask", "ask")]
    [InlineData("pLaN", "plan")]
    public void Normalize_returns_lowercase_trimmed_known_mode(string input, string expected)
    {
        Assert.Equal(expected, CodingRunPermissionModes.NormalizeStored(input));
    }

    [Theory]
    [InlineData(null, "bypass")]   // stored record without a value: created under the legacy contract
    [InlineData("", "bypass")]
    [InlineData("anything", "ask")] // unknown non-empty value: fail closed
    public void NormalizeStored_keeps_legacy_bypass_for_missing_and_fails_closed_for_unknown(string? value, string expected)
    {
        Assert.Equal(expected, CodingRunPermissionModes.NormalizeStored(value));
    }

    [Theory]
    [InlineData(null, "ask")]      // new request that does not state a mode
    [InlineData("", "ask")]
    [InlineData("anything", "ask")]
    [InlineData("bypass", "bypass")]
    public void NormalizeRequested_defaults_new_requests_to_ask_and_honours_explicit_modes(string? value, string expected)
    {
        Assert.Equal(expected, CodingRunPermissionModes.NormalizeRequested(value));
    }

    [Fact]
    public void Constants_are_distinct_lowercase_tokens()
    {
        Assert.Equal("bypass", CodingRunPermissionModes.Bypass);
        Assert.Equal("ask", CodingRunPermissionModes.Ask);
        Assert.Equal("plan", CodingRunPermissionModes.Plan);
        Assert.True(CodingRunPermissionModes.Bypass != CodingRunPermissionModes.Ask);
        Assert.True(CodingRunPermissionModes.Ask != CodingRunPermissionModes.Plan);
        Assert.True(CodingRunPermissionModes.Bypass != CodingRunPermissionModes.Plan);
    }

    [Theory]
    [InlineData("bypass")]
    [InlineData("ask")]
    [InlineData("plan")]
    public void Normalize_is_idempotent_on_canonical_values(string canonical)
    {
        Assert.Equal(canonical, CodingRunPermissionModes.NormalizeStored(CodingRunPermissionModes.NormalizeStored(canonical)));
    }

    [Fact]
    public void Normalize_is_fixed_point_only_for_known_values()
    {
        Assert.False(string.Equals(CodingRunPermissionModes.NormalizeStored("bogus"), "bogus", StringComparison.Ordinal));
        Assert.Equal(CodingRunPermissionModes.NormalizeStored(CodingRunPermissionModes.NormalizeStored("bogus")), CodingRunPermissionModes.Ask);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Stored_missing_value_keeps_the_legacy_bypass_meaning(string? value) =>
        Assert.Equal(CodingRunPermissionModes.Bypass, CodingRunPermissionModes.NormalizeStored(value));

    [Theory]
    [InlineData("bogus")]
    [InlineData("BYPASS; DROP")]
    [InlineData("allow-all")]
    public void Unknown_non_empty_value_fails_closed_to_ask(string value) =>
        Assert.Equal(CodingRunPermissionModes.Ask, CodingRunPermissionModes.NormalizeStored(value));

    [Theory]
    [InlineData(" Plan ", "plan")]
    [InlineData("ASK", "ask")]
    [InlineData("Bypass", "bypass")]
    public void Known_values_are_trimmed_and_lowercased(string raw, string expected) =>
        Assert.Equal(expected, CodingRunPermissionModes.NormalizeStored(raw));
}
