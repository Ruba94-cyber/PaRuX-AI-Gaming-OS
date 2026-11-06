using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services;

/// <summary>
/// One implementation of what the first-run Chat verification can actually use.
/// Corporate Lite ships the CPU backend and MUST NOT report CUDA/Vulkan as
/// prerequisites. A dormant installed backend is launchable, but is not yet
/// proof of healthy inference; the one-time local challenge supplies that proof.
/// </summary>
public static class OnboardingTextReadinessPolicy
{
    public static bool HasInstalledRuntime(
        IEnumerable<ComponentStatus> components, bool corporateMode)
    {
        ArgumentNullException.ThrowIfNull(components);
        return components.Any(c => c.Detected && (corporateMode
            ? c.Id == "llama-server-cpu"
            : c.Id is "llama-server-cuda" or "llama-server-vulkan"));
    }

    public static bool HasHealthyTextBackend(
        IEnumerable<RuntimeBackendStatus> backends, bool corporateMode)
    {
        ArgumentNullException.ThrowIfNull(backends);
        return backends.Any(b => b.Running && b.Healthy && (corporateMode
            ? b.Backend == "text-cpu"
            : b.Backend is "text-cuda" or "text-vulkan"));
    }

    public static string MissingRuntimeMessage(bool corporateMode, bool italian) =>
        corporateMode
            ? italian
                ? "Il runtime CPU incluso non è stato trovato. Estrai nuovamente il pacchetto Corporate verificato."
                : "The included CPU runtime is missing. Re-extract the verified Corporate package."
            : italian
                ? "Installa un runtime di testo locale e un modello Chat tramite Model Hub."
                : "Install a local text runtime and Chat model using Model Hub.";
}
