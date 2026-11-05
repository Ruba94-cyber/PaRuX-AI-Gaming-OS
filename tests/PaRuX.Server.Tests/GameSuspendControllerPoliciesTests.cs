using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameSuspendControllerPoliciesTests
{
    private static GameAutoSuspendRule Rule => new("synthetic-game", true, true, 25, 85);

    [Theory]
    [InlineData(AntiCheatAssessment.Unknown)]
    [InlineData(AntiCheatAssessment.ConfirmedPresent)]
    public void Never_auto_suspends_with_unknown_or_present_anti_cheat(AntiCheatAssessment state)
    {
        var result = GameAutoSuspendPolicy.Evaluate(Rule, true, state, true, 10, 95, "synthetic-game");
        Assert.False(result.Eligible);
    }

    [Fact]
    public void Requires_process_identity_even_when_other_inputs_would_trigger()
    {
        Assert.False(GameAutoSuspendPolicy.Evaluate(
            Rule, false, AntiCheatAssessment.VerifiedAbsent, true, 10, 95, "synthetic-game").Eligible);
    }

    [Fact]
    public void Respects_per_game_conditions_and_thresholds()
    {
        Assert.Equal(GameSuspendTrigger.FocusLost, GameAutoSuspendPolicy.Evaluate(
            Rule, true, AntiCheatAssessment.VerifiedAbsent, true, null, null, "synthetic-game").Trigger);
        Assert.Equal(GameSuspendTrigger.LowBattery, GameAutoSuspendPolicy.Evaluate(
            Rule, true, AntiCheatAssessment.VerifiedAbsent, false, 10, null, "synthetic-game").Trigger);
        Assert.Equal(GameSuspendTrigger.HighTemperature, GameAutoSuspendPolicy.Evaluate(
            Rule, true, AntiCheatAssessment.VerifiedAbsent, false, 60, 90, "synthetic-game").Trigger);
        Assert.False(GameAutoSuspendPolicy.Evaluate(
            Rule, true, AntiCheatAssessment.VerifiedAbsent, false, 60, 40, "synthetic-game").Eligible);
    }

    [Fact]
    public void Known_anticheat_processes_and_user_exclusions_are_blocked()
    {
        Assert.True(GameAutoSuspendPolicy.IsExcludedProcess("EasyAntiCheat_EOS.exe"));
        Assert.True(GameAutoSuspendPolicy.IsExcludedProcess("custom-synthetic.exe",
            new[] { "custom-synthetic" }));
        Assert.False(GameAutoSuspendPolicy.IsExcludedProcess("example-game.exe"));
    }

    [Fact]
    public void Excluded_processes_and_unknown_process_names_block_even_verified_anticheat()
    {
        Assert.False(GameAutoSuspendPolicy.Evaluate(
            Rule, true, AntiCheatAssessment.VerifiedAbsent, true, 10, 95,
            "EasyAntiCheat_EOS.exe").Eligible);
        Assert.False(GameAutoSuspendPolicy.Evaluate(
            Rule, true, AntiCheatAssessment.VerifiedAbsent, true, 10, 95,
            "synthetic-game.exe", new[] { "synthetic-game" }).Eligible);
        Assert.False(GameAutoSuspendPolicy.Evaluate(
            Rule, true, AntiCheatAssessment.VerifiedAbsent, true, 10, 95,
            null).Eligible);
    }

    [Fact]
    public void Curve_and_deadzone_only_preview_normalized_input()
    {
        var preview = new ControllerRemapPreview("synthetic-game", 0.2, ControllerCurve.Precision,
            new System.Collections.Generic.Dictionary<string, string>());
        Assert.Null(ControllerRemapPresenter.Validate(preview));
        Assert.Equal(0, ControllerRemapPresenter.PreviewAxis(0.1, preview));
        Assert.Equal(0.25, ControllerRemapPresenter.PreviewAxis(0.6, preview), precision: 6);
        Assert.Equal(-1, ControllerRemapPresenter.PreviewAxis(-1, preview));
    }

    [Fact]
    public void Refuses_duplicate_mapping_and_unmeasured_latency_is_explicit()
    {
        var mapping = new System.Collections.Generic.Dictionary<string, string>
        {
            ["A"] = "X",
            ["B"] = "X"
        };
        var preview = new ControllerRemapPreview("synthetic", 0.1, ControllerCurve.Linear, mapping);
        Assert.NotNull(ControllerRemapPresenter.Validate(preview));
        var snapshot = new ControllerTestSnapshot(
            new System.Collections.Generic.Dictionary<string, double>(),
            System.Array.Empty<string>(), null, "unknown");
        Assert.Contains("not measured", ControllerRemapPresenter.LatencyText(snapshot));
    }

    [Fact]
    public void Undo_planner_requires_consent_and_never_uninstalls_drivers()
    {
        Assert.Empty(ControllerUndoEverythingPlanner.Steps(false));
        var steps = ControllerUndoEverythingPlanner.Steps(true);
        Assert.Equal(4, steps.Count);
        Assert.DoesNotContain(steps, x => x.Contains("uninstall", System.StringComparison.OrdinalIgnoreCase));
    }
}
