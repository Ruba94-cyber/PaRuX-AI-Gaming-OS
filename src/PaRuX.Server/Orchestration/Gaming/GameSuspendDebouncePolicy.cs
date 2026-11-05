using System;
using System.Collections.Generic;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Transient, caller-owned state. The process stamp must come from a trusted
/// PID + start-time identity check; a process name alone is not sufficient.\n/// Changing a rule snapshot resets elapsed observation time.
/// The monotonic time is injected by the existing host (never DateTime.Now).
/// </summary>
public sealed record GameSuspendDebounceState(
    GameAutoSuspendRule RuleSnapshot, string VerifiedProcessStamp,
    GameSuspendTrigger Trigger, TimeSpan FirstObservedAt, TimeSpan LastObservedAt);

public sealed record GameSuspendDebounceResult(
    GameSuspendDebounceState? NextState, GameAutoSuspendDecision Decision,
    TimeSpan ContinuouslyObserved);

/// <summary>
/// Pure fail-closed stability tracker for event/sensor samples. This never
/// suspends, resumes, reads sensors, polls processes or grants driver access.
/// Host must still ask QuickResumeService to reverify the process and session.
/// </summary>
public static class GameSuspendDebouncePolicy
{
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(2);

    public static GameSuspendDebounceResult Observe(
        GameSuspendDebounceState? prior,
        GameAutoSuspendRule? rule, string? verifiedProcessStamp,
        bool processIdentityVerified, AntiCheatAssessment antiCheat,
        bool focusLost, int? batteryPercent, double? temperatureCelsius,
        string? processName, TimeSpan monotonicNow,
        IReadOnlyCollection<string>? userExcludedProcesses = null)
    {
        static GameSuspendDebounceResult Reset(string reason) =>
            new(null, new GameAutoSuspendDecision(false, GameSuspendTrigger.None, reason), TimeSpan.Zero);

        if (monotonicNow < TimeSpan.Zero
            || string.IsNullOrWhiteSpace(verifiedProcessStamp)
            || !processIdentityVerified)
            return Reset("Trusted process identity and monotonic time are required.");

        GameAutoSuspendDecision proposal = GameAutoSuspendPolicy.Evaluate(
            rule, processIdentityVerified, antiCheat, focusLost,
            batteryPercent, temperatureCelsius, processName, userExcludedProcesses);
        if (!proposal.Eligible || rule is null) return Reset(proposal.Reason);

        bool same = prior is not null
            && prior.RuleSnapshot == rule
            && string.Equals(prior.VerifiedProcessStamp, verifiedProcessStamp, StringComparison.Ordinal)
            && prior.Trigger == proposal.Trigger
            && monotonicNow >= prior.LastObservedAt
            && monotonicNow - prior.LastObservedAt <= MaximumSampleGap
            && monotonicNow >= prior.FirstObservedAt;

        GameSuspendDebounceState next = same
            ? prior!
            : new GameSuspendDebounceState(rule, verifiedProcessStamp,
                proposal.Trigger, monotonicNow, monotonicNow);
        next = next with { LastObservedAt = monotonicNow };

        TimeSpan elapsed = monotonicNow - next.FirstObservedAt;
        GameAutoSuspendDecision stable = GameAutoSuspendPolicy.EvaluateStable(
            rule, processIdentityVerified, antiCheat, focusLost,
            batteryPercent, temperatureCelsius, processName, elapsed,
            userExcludedProcesses);

        return new(next, stable, elapsed);
    }
}
