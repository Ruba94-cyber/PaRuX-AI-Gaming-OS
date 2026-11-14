using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Pure policy decisions for newly discovered model candidates.</summary>
public static class ModelDiscoveryPolicy
{
    public static ModelDiscoveryPolicyDecision Decide(
        string evaluationPolicy,
        string promotionPolicy,
        ModelDiscoveryCandidate candidate,
        bool candidateIsPinnedOrManual,
        bool systemIsIdle,
        bool networkAllowsWork,
        bool gamingIsActive,
        bool candidateIsClearlyBetter = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!IsEvaluationPolicy(evaluationPolicy))
            throw new ArgumentOutOfRangeException(nameof(evaluationPolicy), "Unsupported evaluation policy.");
        if (!IsPromotionPolicy(promotionPolicy))
            throw new ArgumentOutOfRangeException(nameof(promotionPolicy), "Unsupported promotion policy.");

        bool suitable = string.Equals(candidate.Lifecycle, "Candidate", StringComparison.Ordinal)
            && string.Equals(candidate.Disposition, "active", StringComparison.Ordinal);

        ModelDiscoveryPolicyAction autoEvaluate;
        if (gamingIsActive)
            autoEvaluate = new(false, "gaming_active");
        else if (!suitable)
            autoEvaluate = new(false, "candidate_not_suitable");
        else if (evaluationPolicy == ModelDiscoveryPolicyValues.EvaluationAsk)
            autoEvaluate = new(false, "evaluation_policy_ask");
        else if (!systemIsIdle)
            autoEvaluate = new(false, "system_not_idle");
        else if (!networkAllowsWork)
            autoEvaluate = new(false, "network_unavailable");
        else
            autoEvaluate = new(true, "automatic_evaluation_allowed");

        ModelDiscoveryPolicyAction notify;
        if (!suitable)
            notify = new(false, "candidate_not_suitable");
        else if (evaluationPolicy == ModelDiscoveryPolicyValues.EvaluationAsk)
            notify = new(true, "evaluation_policy_ask");
        else if (autoEvaluate.Enabled)
            notify = new(false, "auto_evaluation_selected");
        else
            notify = new(true, "auto_evaluation_deferred");

        (ModelDiscoveryPromotionAction action, string reason) = DecidePromotion(
            promotionPolicy, candidateIsPinnedOrManual, candidateIsClearlyBetter);
        return new ModelDiscoveryPolicyDecision(notify, autoEvaluate, action, reason);
    }

    /// <summary>The promotion half of <see cref="Decide"/>, used on its own by the promotion transaction so the
    /// persisted promotion policy governs the real replace path. Pure; an unknown policy throws.</summary>
    public static (ModelDiscoveryPromotionAction Action, string ReasonCode) DecidePromotion(
        string promotionPolicy, bool candidateIsPinnedOrManual, bool candidateIsClearlyBetter)
    {
        if (!IsPromotionPolicy(promotionPolicy))
            throw new ArgumentOutOfRangeException(nameof(promotionPolicy), "Unsupported promotion policy.");

        if (promotionPolicy == ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual
            && candidateIsPinnedOrManual)
            return (ModelDiscoveryPromotionAction.NeverReplace, "pinned_or_manual_choice");

        if (promotionPolicy == ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing)
            return (ModelDiscoveryPromotionAction.AskBeforeReplacing, "promotion_policy_ask_before_replacing");

        if (candidateIsPinnedOrManual)
            return (ModelDiscoveryPromotionAction.AskBeforeReplacing, "pinned_or_manual_requires_confirmation");

        if (promotionPolicy == ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual)
            return (ModelDiscoveryPromotionAction.AskBeforeReplacing, "promotion_policy_preserves_pinned_or_manual_only");

        return candidateIsClearlyBetter
            ? (ModelDiscoveryPromotionAction.AutoPromote, "candidate_clearly_better")
            : (ModelDiscoveryPromotionAction.AskBeforeReplacing, "candidate_not_clearly_better");
    }

    internal static bool IsValidSettings(ModelDiscoveryPolicySettings? settings) =>
        settings is not null
        && settings.SchemaVersion == 1
        && IsEvaluationPolicy(settings.EvaluationPolicy)
        && IsPromotionPolicy(settings.PromotionPolicy);

    private static bool IsEvaluationPolicy(string? policy) =>
        policy is ModelDiscoveryPolicyValues.EvaluationAsk
            or ModelDiscoveryPolicyValues.EvaluationAlwaysEvaluateSuitable;

    private static bool IsPromotionPolicy(string? policy) =>
        policy is ModelDiscoveryPolicyValues.PromotionAskBeforeReplacing
            or ModelDiscoveryPolicyValues.PromotionAutoPromoteClearlyBetter
            or ModelDiscoveryPolicyValues.PromotionNeverReplacePinnedOrManual;
}
