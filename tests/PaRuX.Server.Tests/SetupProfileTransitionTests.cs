using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SetupProfileTransitionTests
{
    private static SetupRunningServices Running(SetupProfile p) => SetupRunningServices.Of(SetupProfilePolicy.Plan(p));

    private static string Describe(SetupTransitionPlan plan) =>
        string.Join(",", plan.Steps.Select(s => (s.Kind == SetupStepKind.Start ? "+" : "-") + s.Service));

    [Fact]
    public void Both_to_gaming_only_stops_ai_and_agents_keeps_gaming()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.Both), SetupProfile.GamingOnly, false, false);
        Assert.Equal("-LocalAi,-AgentWorkers", Describe(plan));
        Assert.Equal(SetupProfile.GamingOnly, plan.Target);
    }

    [Fact]
    public void Gaming_only_to_both_starts_ai_and_agents()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.GamingOnly), SetupProfile.Both, false, false);
        Assert.Equal("+LocalAi,+AgentWorkers", Describe(plan));
        Assert.All(plan.Steps, s => Assert.Equal(SetupStepKind.Start, s.Kind));
    }

    [Fact]
    public void Ai_only_to_gaming_only_stops_first_then_starts()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.AiOnly), SetupProfile.GamingOnly, false, false);
        Assert.Equal("-LocalAi,-AgentWorkers,+Gaming", Describe(plan));
        Assert.Equal(SetupStepKind.Stop, plan.Steps[0].Kind);
        Assert.Equal(SetupStepKind.Start, plan.Steps[^1].Kind);
    }

    [Theory]
    [InlineData(SetupProfile.Both)]
    [InlineData(SetupProfile.AiOnly)]
    [InlineData(SetupProfile.GamingOnly)]
    public void Applying_the_running_profile_again_is_a_no_op(SetupProfile profile)
    {
        var plan = SetupProfileTransition.Plan(Running(profile), profile, false, false);
        Assert.True(plan.IsNoOp);
    }

    [Fact]
    public void Corporate_forces_ai_only()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.Both), SetupProfile.GamingOnly, true, false);
        Assert.Equal(SetupProfile.AiOnly, plan.Target);
        Assert.True(plan.ForcedToAiOnly);
        Assert.Equal("-Gaming", Describe(plan));
    }

    [Fact]
    public void Running_game_defers_stopping_the_gaming_service()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.Both), SetupProfile.AiOnly, false, gameActive: true);
        Assert.True(plan.IsNoOp);
        Assert.Equal(new[] { SetupService.Gaming }, plan.Deferred);
    }

    [Fact]
    public void Half_applied_state_is_repaired()
    {
        // Gaming only wanted, but the AI workers are still up from an earlier failure.
        var running = new SetupRunningServices(false, true, true);
        var plan = SetupProfileTransition.Plan(running, SetupProfile.GamingOnly, false, false);
        Assert.Equal("-AgentWorkers", Describe(plan));
    }

    [Fact]
    public async Task Success_applies_every_step_in_order()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.AiOnly), SetupProfile.GamingOnly, false, false);
        var ran = new List<SetupTransitionStep>();
        var result = await SetupProfileTransition.ApplyAsync(plan, SetupProfile.AiOnly,
            (s, _) => { ran.Add(s); return Task.FromResult(true); }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(SetupProfile.GamingOnly, result.EffectiveProfile);
        Assert.Equal(plan.Steps, ran);
    }

    [Fact]
    public async Task Failure_rolls_back_applied_steps_in_reverse_and_keeps_previous_profile()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.AiOnly), SetupProfile.GamingOnly, false, false);
        var ran = new List<SetupTransitionStep>();
        var gaming = new SetupTransitionStep(SetupService.Gaming, SetupStepKind.Start);
        var result = await SetupProfileTransition.ApplyAsync(plan, SetupProfile.AiOnly, (s, _) =>
        {
            ran.Add(s);
            return Task.FromResult(s != gaming);
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(SetupProfile.AiOnly, result.EffectiveProfile);
        Assert.Equal(gaming, result.Failed);
        Assert.True(result.RollbackComplete);
        // forward: stop LocalAi, stop Agents, start Gaming(fails); undo: start Agents, start LocalAi
        Assert.Equal(new[]
        {
            new SetupTransitionStep(SetupService.AgentWorkers, SetupStepKind.Start),
            new SetupTransitionStep(SetupService.LocalAi, SetupStepKind.Start)
        }, result.RolledBack);
        Assert.Equal(5, ran.Count);
    }

    [Fact]
    public async Task Exception_counts_as_failure_and_rollback_continues_past_a_failing_undo()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.AiOnly), SetupProfile.GamingOnly, false, false);
        int calls = 0;
        var result = await SetupProfileTransition.ApplyAsync(plan, SetupProfile.AiOnly, (s, _) =>
        {
            calls++;
            if (calls == 3) throw new InvalidOperationException("boom");
            if (calls == 4) throw new InvalidOperationException("undo boom");
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.RollbackComplete);
        Assert.Single(result.RolledBack);       // the second undo still ran
        Assert.Equal(5, calls);
        Assert.DoesNotContain("boom", result.Message);   // no exception text leaks
    }

    [Fact]
    public async Task Cancellation_rolls_back()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.Both), SetupProfile.GamingOnly, false, false);
        using var cts = new CancellationTokenSource();
        var result = await SetupProfileTransition.ApplyAsync(plan, SetupProfile.Both, (s, _) =>
        {
            cts.Cancel();
            return Task.FromResult(true);
        }, cts.Token);
        Assert.False(result.Success);
        Assert.Equal(SetupProfile.Both, result.EffectiveProfile);
        Assert.Single(result.RolledBack);
    }

    [Fact]
    public async Task No_op_plan_succeeds_without_running_anything()
    {
        var plan = SetupProfileTransition.Plan(Running(SetupProfile.Both), SetupProfile.Both, false, false);
        var result = await SetupProfileTransition.ApplyAsync(plan, SetupProfile.Both,
            (_, _) => throw new InvalidOperationException(), CancellationToken.None);
        Assert.True(result.Success);
    }
}
