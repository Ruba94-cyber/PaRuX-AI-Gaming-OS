using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-package finding (2026-11-14): Corporate chat answered 503 "No candidate fits the current memory and Execution
/// Profile budget" for 3 of 4 questions on a machine with 10 GB free. The router judged the CPU-only edition with a
/// 32k-context GPU estimate against the instantaneous free RAM. Corporate now uses a fixed, per-profile capacity check
/// of the real CPU launch footprint. The onboarding failure text also showed English in Italian and a Vulkan-installer
/// hint that does not exist in the Corporate package.
/// </summary>
public sealed class CorporateFitAndOnboardingTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void Corporate_fit_is_a_fixed_capacity_check_against_total_ram()
    {
        // Qwen3-4B Q4_K_M at the 4,096-token CPU launch is about 3.9 GiB.
        double needed = 3.9 * 1024;
        HardwareFitEstimate onPc = CorporateFitPolicy.Estimate(needed, 26 * Gib, 86);
        Assert.True(onPc.Compatible);
        Assert.Equal(26d * 1024 * 0.86, onPc.AvailableMib, 3);

        // The same inputs always give the same verdict; there is no free-RAM input to vary.
        for (int i = 0; i < 5; i++)
            Assert.Equal(onPc, CorporateFitPolicy.Estimate(needed, 26 * Gib, 86));

        Assert.True(CorporateFitPolicy.Estimate(needed, 8 * Gib, 86).Compatible);
        Assert.False(CorporateFitPolicy.Estimate(needed, 4 * Gib, 86).Compatible);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5)]
    public void Corporate_fit_never_admits_on_a_bad_budget(double percent)
    {
        Assert.False(CorporateFitPolicy.Estimate(1, 16 * Gib, percent).Compatible);
    }

    [Fact]
    public void Runtime_manager_uses_the_corporate_fit_before_the_gpu_estimate()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir)!;
        string source = File.ReadAllText(Path.Combine(dir!, "src", "PaRuX.Server", "RuntimeManagerService.cs"));
        int method = source.IndexOf("public async Task<HardwareFitEstimate?> EstimateHardwareFitAsync", StringComparison.Ordinal);
        int corporate = source.IndexOf("CorporateFitPolicy.Estimate(", method, StringComparison.Ordinal);
        int gpu = source.IndexOf("GetVramMibAsync", method, StringComparison.Ordinal);
        Assert.True(method > 0 && corporate > method && gpu > corporate);
    }

    [Fact]
    public void Missing_runtime_in_corporate_never_points_at_vulkan_or_an_installer_and_is_italian_in_italian()
    {
        var sm = new OnboardingStateMachine { CorporateEdition = true };
        ReadinessAssessment a = sm.EvaluateReadiness(ChatReadinessEvidence.Create(
            runtimeHealthy: false, runtimeSummary: "Bundled CPU text runtime missing", modelAvailable: false,
            modelSummary: null, probeSucceeded: false, probeSummary: "unverified", errorMessage: null));

        Assert.False(a.IsReady);
        Assert.DoesNotContain("Vulkan", a.NextAction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Install with PaRuX", a.NextAction, StringComparison.OrdinalIgnoreCase);

        string summary = OnboardingReadinessText.Localize(a.Summary, italian: true);
        string next = OnboardingReadinessText.Localize(a.NextAction, italian: true);
        Assert.StartsWith("Configurazione incompleta: Nessun motore di testo", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Setup is incomplete", summary, StringComparison.Ordinal);
        Assert.Contains("pacchetto Corporate", next, StringComparison.Ordinal);
        Assert.DoesNotContain("Vulkan", next, StringComparison.OrdinalIgnoreCase);

        // English stays English.
        Assert.Equal(a.Summary, OnboardingReadinessText.Localize(a.Summary, italian: false));
    }

    [Fact]
    public void Italian_unproven_items_in_the_no_runtime_path_are_never_English()
    {
        var sm = new OnboardingStateMachine { CorporateEdition = true };
        sm.EvaluateReadiness(ChatReadinessEvidence.Create(
            runtimeHealthy: false, runtimeSummary: "Bundled CPU text runtime missing", modelAvailable: false,
            modelSummary: null, probeSucceeded: false, probeSummary: "unverified", errorMessage: "connection refused"));

        foreach (string item in sm.GetUnprovenItems())
        {
            string it = OnboardingReadinessText.Localize(item, italian: true);
            Assert.NotEqual(item, it);
            Assert.DoesNotContain("not been", it, StringComparison.Ordinal);
            Assert.DoesNotContain("is installed", it, StringComparison.Ordinal);
        }
        Assert.Contains("non è riuscito (connection refused)", OnboardingReadinessText.Localize("Local chat health probe has not succeeded (connection refused).", true), StringComparison.Ordinal);
        Assert.StartsWith("Configurazione incompleta", OnboardingReadinessText.Localize("Setup is incomplete; unproven readiness accepted by user.", true), StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_edition_keeps_its_installer_hint()
    {
        var sm = new OnboardingStateMachine();
        ReadinessAssessment a = sm.EvaluateReadiness(ChatReadinessEvidence.Create(
            runtimeHealthy: false, runtimeSummary: "none", modelAvailable: false,
            modelSummary: null, probeSucceeded: false, probeSummary: "unverified", errorMessage: null));
        Assert.Contains("Vulkan", a.NextAction, StringComparison.Ordinal);
        Assert.Contains("Individua esistente", OnboardingReadinessText.Localize(a.NextAction, italian: true), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Local chat health probe failed (timeout).", "Controllo di salute della chat locale non riuscito (timeout).")]
    [InlineData("Component 'llama-server-cpu' changed; readiness must be re-evaluated.", "Il componente 'llama-server-cpu' è cambiato: la prontezza va valutata di nuovo.")]
    [InlineData("No chat model is installed or loaded.", "Nessun modello di chat è installato o caricato.")]
    public void Dynamic_readiness_reasons_are_translated(string english, string italian) =>
        Assert.Equal(italian, OnboardingReadinessText.Localize(english, italian: true));
}
