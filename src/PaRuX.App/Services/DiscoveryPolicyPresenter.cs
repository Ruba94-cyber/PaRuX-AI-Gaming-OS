using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public sealed record PolicyControlPresentation(
        string Label,
        string Explanation,
        bool IsEnabled,
        string Value,
        string? UnavailableReason = null);

    public sealed record PromotionOptionPresentation(
        string Value,
        string Label,
        string Explanation);

    public sealed record DiscoveryPolicyPresentation(
        PolicyControlPresentation AutoEvaluate,
        PolicyControlPresentation Promotion,
        IReadOnlyList<PromotionOptionPresentation> PromotionOptions,
        string StatusText);

    public sealed record DiscoveryPolicyState(
        string AutoEvaluate = DiscoveryPolicyPresenter.DefaultAutoEvaluate,
        string PromotionPolicy = DiscoveryPolicyPresenter.DefaultPromotion,
        bool GamingActive = false,
        string? WarningCode = null);

    public interface IDiscoveryPolicyClient
    {
        Task<DiscoveryPolicyState> GetPolicyAsync(CancellationToken ct = default);
        Task<DiscoveryPolicyState> SetAutoEvaluateAsync(string autoEvaluate, CancellationToken ct = default);
        Task<DiscoveryPolicyState> SetPromotionPolicyAsync(string promotionPolicy, CancellationToken ct = default);
    }

    public sealed class StubDiscoveryPolicyClient : IDiscoveryPolicyClient
    {
        private string _autoEvaluate = DiscoveryPolicyPresenter.DefaultAutoEvaluate;
        private string _promotionPolicy = DiscoveryPolicyPresenter.DefaultPromotion;
        private bool _gamingActive;
        private readonly object _lock = new();

        public StubDiscoveryPolicyClient(bool initialGamingActive = false)
        {
            _gamingActive = initialGamingActive;
        }

        public void SetGamingActive(bool active)
        {
            lock (_lock) _gamingActive = active;
        }

        public Task<DiscoveryPolicyState> GetPolicyAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(new DiscoveryPolicyState(_autoEvaluate, _promotionPolicy, _gamingActive));
            }
        }

        public Task<DiscoveryPolicyState> SetAutoEvaluateAsync(string autoEvaluate, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _autoEvaluate = DiscoveryPolicyPresenter.NormalizeAutoEvaluate(autoEvaluate);
                return Task.FromResult(new DiscoveryPolicyState(_autoEvaluate, _promotionPolicy, _gamingActive));
            }
        }

        public Task<DiscoveryPolicyState> SetPromotionPolicyAsync(string promotionPolicy, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _promotionPolicy = DiscoveryPolicyPresenter.NormalizePromotion(promotionPolicy);
                return Task.FromResult(new DiscoveryPolicyState(_autoEvaluate, _promotionPolicy, _gamingActive));
            }
        }
    }

    public static class DiscoveryPolicyPresenter
    {
        public const string AutoEvaluateAsk = "ask";
        public const string AutoEvaluateAuto = "auto";

        public const string PromotionAsk = "ask";
        public const string PromotionClearlyBetter = "clearly_better";
        public const string PromotionNever = "never";

        public const string DefaultAutoEvaluate = AutoEvaluateAsk;
        public const string DefaultPromotion = PromotionAsk;

        public const string GamingPausedReason = "Paused while Gaming is active";

        public const string AutoEvaluateLabel = "Evaluate suitable models automatically";
        public const string PromotionPolicyLabel = "Promotion policy";

        public const string SavedStatusText = "Saved to PaRuX Core.";

        private static string T(string en, string it) => UiText.Pick(en, it);

        public static readonly IReadOnlyList<PromotionOptionPresentation> PromotionOptions = new PromotionOptionPresentation[]
        {
            new(PromotionAsk, "Ask before replacing", "Ask before switching your active model to a candidate."),
            new(PromotionClearlyBetter, "Auto-promote only when clearly better", "Automatically switch only when a candidate demonstrates strictly better quality and speed."),
            new(PromotionNever, "Never replace pinned or manual choices", "Never replace pinned or manual choices automatically.")
        };

        private static string PromotionLabelText(string normalized) => normalized switch
        {
            PromotionClearlyBetter => T("Auto-promote only when clearly better", "Promuovi in automatico solo se chiaramente migliore"),
            PromotionNever => T("Never replace pinned or manual choices", "Non sostituire mai le scelte fissate o manuali"),
            _ => T("Ask before replacing", "Chiedi prima di sostituire")
        };

        /// <summary>Promotion options with labels and explanations in the active UI language.</summary>
        public static IReadOnlyList<PromotionOptionPresentation> LocalizedPromotionOptions() => new PromotionOptionPresentation[]
        {
            new(PromotionAsk, PromotionLabelText(PromotionAsk), GetPromotionExplanation(PromotionAsk)),
            new(PromotionClearlyBetter, PromotionLabelText(PromotionClearlyBetter), GetPromotionExplanation(PromotionClearlyBetter)),
            new(PromotionNever, PromotionLabelText(PromotionNever), GetPromotionExplanation(PromotionNever))
        };

        public static string NormalizeAutoEvaluate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultAutoEvaluate;
            string trimmed = value.Trim();
            if (string.Equals(trimmed, AutoEvaluateAuto, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "automatic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase))
            {
                return AutoEvaluateAuto;
            }
            return AutoEvaluateAsk;
        }

        public static string NormalizePromotion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultPromotion;
            string trimmed = value.Trim();
            if (string.Equals(trimmed, PromotionClearlyBetter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "when_clearly_better", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "better", StringComparison.OrdinalIgnoreCase))
            {
                return PromotionClearlyBetter;
            }
            if (string.Equals(trimmed, PromotionNever, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "never_replace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "manual", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "pinned_only", StringComparison.OrdinalIgnoreCase))
            {
                return PromotionNever;
            }
            return PromotionAsk;
        }

        public static bool IsAutoEvaluateOn(string? value) =>
            string.Equals(NormalizeAutoEvaluate(value), AutoEvaluateAuto, StringComparison.Ordinal);

        public static string ToAutoEvaluateValue(bool isOn) =>
            isOn ? AutoEvaluateAuto : AutoEvaluateAsk;

        public static string GetAutoEvaluateLabel() => T(AutoEvaluateLabel, "Valuta automaticamente i modelli adatti");

        public static string GetAutoEvaluateExplanation(string? value, bool gamingActive = false)
        {
            if (gamingActive) return GetGamingPausedReason();
            return IsAutoEvaluateOn(value)
                ? T("Automatically benchmark newly discovered models that match this machine's hardware.",
                    "Esegue automaticamente il benchmark dei modelli appena scoperti compatibili con l'hardware di questo PC.")
                : T("Ask before running local benchmarks on newly discovered model candidates.",
                    "Chiede conferma prima di eseguire benchmark locali sui nuovi modelli candidati.");
        }

        public static bool IsAutoEvaluateEnabled(bool gamingActive) => !gamingActive;

        public static string GetGamingPausedReason() => T(GamingPausedReason, "In pausa mentre la Modalità Gaming è attiva");

        public static string GetPromotionLabel() => T(PromotionPolicyLabel, "Criterio di promozione");

        public static string GetPromotionExplanation(string? value)
        {
            string normalized = NormalizePromotion(value);
            return normalized switch
            {
                PromotionClearlyBetter => T("Automatically switch only when a candidate demonstrates strictly better quality and speed.",
                    "Sostituisce automaticamente solo se un candidato dimostra qualità e velocità nettamente migliori."),
                PromotionNever => T("Never replace pinned or manual choices automatically.",
                    "Non sostituisce mai automaticamente le scelte fissate o manuali."),
                _ => T("Ask before switching your active model to a candidate.",
                    "Chiede conferma prima di sostituire il modello attivo con un candidato.")
            };
        }

        public static bool IsPromotionEnabled(bool gamingActive = false) => true;

        public static IReadOnlyList<PromotionOptionPresentation> GetPromotionOptions() => LocalizedPromotionOptions();

        public static PolicyControlPresentation PresentAutoEvaluate(string? value, bool gamingActive = false)
        {
            string normalized = NormalizeAutoEvaluate(value);
            bool enabled = IsAutoEvaluateEnabled(gamingActive);
            string explanation = GetAutoEvaluateExplanation(normalized, gamingActive);
            string? reason = gamingActive ? GetGamingPausedReason() : null;
            return new PolicyControlPresentation(GetAutoEvaluateLabel(), explanation, enabled, normalized, reason);
        }

        public static PolicyControlPresentation PresentAutoEvaluate(bool isOn, bool gamingActive = false) =>
            PresentAutoEvaluate(ToAutoEvaluateValue(isOn), gamingActive);

        public static PolicyControlPresentation PresentPromotion(string? value, bool gamingActive = false)
        {
            string normalized = NormalizePromotion(value);
            string explanation = GetPromotionExplanation(normalized);
            bool enabled = IsPromotionEnabled(gamingActive);
            return new PolicyControlPresentation(GetPromotionLabel(), explanation, enabled, normalized, null);
        }

        public static DiscoveryPolicyPresentation Present(
            string? autoEvaluate, string? promotionPolicy, bool gamingActive, string? status = null)
        {
            PolicyControlPresentation autoPresentation = PresentAutoEvaluate(autoEvaluate, gamingActive);
            PolicyControlPresentation promotionPresentation = PresentPromotion(promotionPolicy, gamingActive);
            string statusText = status ?? string.Empty;
            return new DiscoveryPolicyPresentation(autoPresentation, promotionPresentation, LocalizedPromotionOptions(), statusText);
        }

        public static DiscoveryPolicyPresentation Present(
            DiscoveryPolicyState state, bool gamingActive, string? status = null)
        {
            ArgumentNullException.ThrowIfNull(state);
            return Present(state.AutoEvaluate, state.PromotionPolicy, gamingActive || state.GamingActive, status);
        }

        public static string FormatLoadingStatus() =>
            T("Loading the discovery policy from PaRuX Core...", "Caricamento dei criteri di scoperta da PaRuX Core...");

        public static string FormatSavingStatus() =>
            T("Saving to PaRuX Core...", "Salvataggio in PaRuX Core...");

        public static string FormatSavedStatus() =>
            T(SavedStatusText, "Salvato in PaRuX Core.");

        public static string FormatCorruptFileWarning() =>
            T("The saved policy file could not be read, so safe defaults are shown. Saving a change replaces it.",
              "Il file dei criteri salvato non è leggibile, quindi vengono mostrati i valori predefiniti sicuri. Salvando una modifica verrà sostituito.");

        public static string FormatLoadErrorStatus(string error) =>
            T("Couldn't load the candidate discovery policy: ", "Impossibile caricare i criteri di scoperta dei candidati: ") + ErrorMessage(error);

        public static string FormatErrorStatus(string error) =>
            T("Couldn't save candidate discovery policy: ", "Impossibile salvare i criteri di scoperta dei candidati: ") + ErrorMessage(error);

        public static string FormatErrorStatus(string action, string error) =>
            T("Couldn't save ", "Impossibile salvare ") + action + ": " + ErrorMessage(error);

        private static string ErrorMessage(string? error) =>
            string.IsNullOrWhiteSpace(error)
                ? T("An unexpected error occurred.", "Si è verificato un errore imprevisto.")
                : error.Trim();

        public static bool ResolveRevertAutoEvaluate(bool requested, bool lastConfirmedState) => lastConfirmedState;

        public static string ResolveRevertPromotion(string requested, string lastConfirmedPolicy) =>
            NormalizePromotion(lastConfirmedPolicy);
    }
}
