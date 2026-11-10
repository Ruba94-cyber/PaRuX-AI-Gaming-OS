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
        IEnumerable<ComponentStatus> components, bool corporateMode, bool standardPortableMode = false)
    {
        ArgumentNullException.ThrowIfNull(components);
        return components.Any(c => c.Detected && (corporateMode
            ? c.Id == "llama-server-cpu"
            : (c.Id is "llama-server-cuda" or "llama-server-vulkan")
                || (standardPortableMode && c.Id == "llama-server-cpu")));
    }

    public static bool HasHealthyTextBackend(
        IEnumerable<RuntimeBackendStatus> backends, bool corporateMode, bool standardPortableMode = false)
    {
        ArgumentNullException.ThrowIfNull(backends);
        return backends.Any(b => b.Running && b.Healthy && (corporateMode
            ? b.Backend == "text-cpu"
            : (b.Backend is "text-cuda" or "text-vulkan")
                || (standardPortableMode && b.Backend == "text-cpu")));
    }

    public static string MissingModelMessage(bool corporateMode, bool italian) =>
        corporateMode
            ? italian
                ? "Non è disponibile un modello GGUF locale. Controlla il pacchetto Corporate oppure importa un modello dalla libreria."
                : "No local Chat GGUF is available. Check the Corporate package or import a model from the library."
            : italian
                ? "Non è disponibile un modello Chat. Seleziona o importa un modello dal Model Hub."
                : "No Chat model is available. Select or import one from Model Hub.";

    public static string MissingRuntimeMessage(bool corporateMode, bool italian,
        bool standardPortableMode = false) =>
        !corporateMode && standardPortableMode
            ? italian
                ? "Il motore CPU/Vulkan incluso nella Portable non è disponibile. Estrai nuovamente lo ZIP verificato."
                : "The bundled CPU/Vulkan engine is missing. Re-extract the verified Portable ZIP."
        : corporateMode
            ? italian
                ? "Il runtime CPU incluso non è stato trovato. Estrai nuovamente il pacchetto Corporate verificato."
                : "The included CPU runtime is missing. Re-extract the verified Corporate package."
            : italian
                ? "Installa un runtime di testo locale e un modello Chat tramite Model Hub."
                : "Install a local text runtime and Chat model using Model Hub.";
}
