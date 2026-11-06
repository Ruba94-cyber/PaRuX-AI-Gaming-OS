using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services;

/// <summary>The three background consumers that a <see cref="SetupProfile"/> switches on or off at run time.</summary>
public enum SetupService { LocalAi, Gaming, AgentWorkers }

public enum SetupStepKind { Start, Stop }

public sealed record SetupTransitionStep(SetupService Service, SetupStepKind Kind)
{
    public SetupTransitionStep Inverse => this with { Kind = Kind == SetupStepKind.Start ? SetupStepKind.Stop : SetupStepKind.Start };
}

/// <summary>Which services are running right now (the truth the runner tracks), not what the stored profile says.</summary>
public sealed record SetupRunningServices(bool LocalAi, bool Gaming, bool AgentWorkers)
{
    public static SetupRunningServices Of(SetupProfilePlan plan) => new(plan.StartLocalAi, plan.StartGaming, plan.StartAgentWorkers);

    public bool IsRunning(SetupService service) => service switch
    {
        SetupService.LocalAi => LocalAi,
        SetupService.Gaming => Gaming,
        _ => AgentWorkers
    };

    public SetupRunningServices With(SetupService service, bool running) => service switch
    {
        SetupService.LocalAi => this with { LocalAi = running },
        SetupService.Gaming => this with { Gaming = running },
        _ => this with { AgentWorkers = running }
    };
}

/// <summary>An ordered, minimal list of start/stop steps that moves the running services to a profile. Stops come first so
/// the GPU/RAM are free before anything starts. Nothing in a plan deletes data or installs software.</summary>
public sealed record SetupTransitionPlan(
    SetupProfile Requested,
    SetupProfile Target,
    bool ForcedToAiOnly,
    IReadOnlyList<SetupTransitionStep> Steps,
    IReadOnlyList<SetupService> Deferred,
    string Note)
{
    public bool IsNoOp => Steps.Count == 0;
}

public sealed record SetupTransitionResult(
    bool Success,
    SetupProfile EffectiveProfile,
    IReadOnlyList<SetupTransitionStep> Applied,
    IReadOnlyList<SetupTransitionStep> RolledBack,
    SetupTransitionStep? Failed,
    bool RollbackComplete,
    string Message);

/// <summary>
/// Pure planner and executor for run-time profile switches (AI only / Gaming only / Both). The plan is derived from the
/// services that are actually running, so applying the same profile twice does nothing and a half-applied state is repaired.
/// The executor rolls back every applied step if one fails, so the PC is never left in a mixed state the profile does not describe.
/// </summary>
public static class SetupProfileTransition
{
    /// <summary>Corporate Lite is AI/documents only: any request resolves to AI only.</summary>
    public static SetupProfile Resolve(SetupProfile requested, bool corporateMode) =>
        corporateMode ? SetupProfile.AiOnly : requested;

    public static SetupTransitionPlan Plan(SetupRunningServices running, SetupProfile requested, bool corporateMode, bool gameActive)
    {
        SetupProfile target = Resolve(requested, corporateMode);
        bool forced = corporateMode && requested != SetupProfile.AiOnly;
        SetupProfilePlan wanted = SetupProfilePolicy.Plan(target, corporateMode);
        var desired = SetupRunningServices.Of(wanted);

        var stops = new List<SetupTransitionStep>();
        var starts = new List<SetupTransitionStep>();
        var deferred = new List<SetupService>();
        foreach (SetupService service in Enum.GetValues<SetupService>())
        {
            bool have = running.IsRunning(service);
            bool want = desired.IsRunning(service);
            if (have == want) continue;
            if (want) { starts.Add(new SetupTransitionStep(service, SetupStepKind.Start)); continue; }

            // A running game owns the Gaming hard-off: its watcher must keep restoring the app when the game ends.
            if (service == SetupService.Gaming && gameActive) { deferred.Add(service); continue; }
            stops.Add(new SetupTransitionStep(service, SetupStepKind.Stop));
        }

        var steps = stops.Concat(starts).ToList();
        string note = forced
            ? "Corporate Lite is AI and documents only, so AI only was kept."
            : deferred.Count > 0
                ? "A game is running, so the Gaming service keeps running until it ends or PaRuX restarts."
                : steps.Count == 0 ? "Nothing to change." : string.Empty;
        return new SetupTransitionPlan(requested, target, forced, steps, deferred, note);
    }

    /// <summary>
    /// Runs the plan step by step. A thrown exception, a <c>false</c> return or cancellation counts as failure: the steps that
    /// already ran are undone in reverse order (best effort, every undo attempted) and <see cref="SetupTransitionResult.EffectiveProfile"/>
    /// is <paramref name="previous"/>, so the caller persists nothing new.
    /// </summary>
    public static async Task<SetupTransitionResult> ApplyAsync(
        SetupTransitionPlan plan, SetupProfile previous,
        Func<SetupTransitionStep, CancellationToken, Task<bool>> run, CancellationToken ct)
    {
        var applied = new List<SetupTransitionStep>();
        SetupTransitionStep? failed = null;
        string failure = string.Empty;
        foreach (SetupTransitionStep step in plan.Steps)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!await run(step, ct).ConfigureAwait(false)) { failed = step; failure = "the step reported a failure"; break; }
                applied.Add(step);
            }
            catch (Exception ex)
            {
                failed = step;
                failure = ex is OperationCanceledException ? "cancelled" : ex.GetType().Name;
                break;
            }
        }

        if (failed is null)
            return new SetupTransitionResult(true, plan.Target, applied, Array.Empty<SetupTransitionStep>(), null, true, plan.Note);

        var rolledBack = new List<SetupTransitionStep>();
        bool complete = true;
        foreach (SetupTransitionStep step in Enumerable.Reverse(applied))
        {
            try
            {
                if (await run(step.Inverse, CancellationToken.None).ConfigureAwait(false)) rolledBack.Add(step.Inverse);
                else complete = false;
            }
            catch { complete = false; }
        }

        string message = "Could not switch (" + failed.Service + " " + failed.Kind.ToString().ToLowerInvariant() + ": " + failure + "). "
            + (complete ? "Everything was put back as it was." : "Some services could not be put back; restart PaRuX to settle them.");
        return new SetupTransitionResult(false, previous, applied, rolledBack, failed, complete, message);
    }
}
