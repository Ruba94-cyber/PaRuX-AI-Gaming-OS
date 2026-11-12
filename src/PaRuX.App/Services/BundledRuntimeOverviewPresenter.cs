using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class BundledComponentItem
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string BundledSizeText { get; init; } = string.Empty;
        public long DownloadSizeBytes => 0L;
        public string DownloadSizeText => "0 B (bundled in installer)";
        public bool IsStaged { get; set; }
        public string Description { get; init; } = string.Empty;
    }

    public sealed class DownloadableComponentItem
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public long EstimatedSizeBytes { get; set; }
        public string EstimatedSizeText { get; set; } = string.Empty;
        public bool IsRequired { get; init; }
        public string WhenDownloaded { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
    }

    public sealed class FirstInstallEffortOverview
    {
        public bool HasWorkingTextPathWithoutDownload { get; init; }
        public string Headline { get; init; } = string.Empty;
        public string Subhead { get; init; } = string.Empty;
        public IReadOnlyList<BundledComponentItem> BundledItems { get; init; } = Array.Empty<BundledComponentItem>();
        public IReadOnlyList<DownloadableComponentItem> DownloadableItems { get; init; } = Array.Empty<DownloadableComponentItem>();
        public string HashCheckGuidance { get; init; } = string.Empty;
        public string PowerShellHashCommand { get; init; } = string.Empty;
        public string CmdHashCommand { get; init; } = string.Empty;
        public string AuthenticodeCheckCommand { get; init; } = string.Empty;
        public string DocReference { get; init; } = string.Empty;
    }

    /// <summary>
    /// Pure, unit-testable presentation helper for first-install effort, bundled runtime visibility,
    /// and download size expectations on the first screen.
    ///
    /// Implements UX spec row 'ux:First-install effort and bundled runtime':
    /// The installer ships a baseline text engine but still requires model selection and a verified chat turn.
    /// The first screen says what is already bundled and what will be downloaded, with sizes.
    /// The user can check the installer hash from the docs.
    /// </summary>
    public static class BundledRuntimeOverviewPresenter
    {
        private static string T(string en, string it) => PaRuX.Services.UiText.Pick(en, it);
        public const string BundledVulkanId = "llama-server-vulkan";
        public const string BundledUvId = "uv";
        public const string BundledAppId = "parux-engine";

        public const string DownloadableModelId = "first-model";
        public const string DownloadableCudaId = "llama-server-cuda";
        public const string DownloadableComfyUiId = "comfyui";

        public const long CudaRuntimeSizeBytes = 420_000_000L;          // ~400 MB CUDA pack
        public const long ComfyUiSizeBytes = 1_600_000_000L;            // ~1.5 GB ComfyUI pack

        public const string PowerShellVerificationCommand = "Get-FileHash -Algorithm SHA256 .\\PaRuX-*.msi";
        public const string CmdVerificationCommand = "certutil -hashfile PaRuX-*.msi SHA256";
        public const string AuthenticodeVerificationCommand = "Get-AuthenticodeSignature .\\PaRuX-*.msi";
        public const string DocumentationPath = "docs/AI_LOCAL_SETUP.md";

        public static FirstInstallEffortOverview BuildOverview(
            IReadOnlyList<ComponentStatus>? components,
            FirstModelAdvice? modelAdvice = null)
        {
            bool vulkanDetected = IsComponentDetected(components, BundledVulkanId);
            // On NVIDIA GPUs that support it the MSI bootstraps the bundled CUDA 13.4 build instead of Vulkan, so a
            // detected CUDA text engine is just as much "the included engine" as Vulkan.
            bool cudaDetected = IsComponentDetected(components, DownloadableCudaId);
            bool uvDetected = IsComponentDetected(components, BundledUvId);

            // Bundled items in the signed installer
            var bundledList = new List<BundledComponentItem>
            {
                new()
                {
                    Id = BundledVulkanId,
                    DisplayName = "llama.cpp (Vulkan) Text Engine",
                    Category = "Text Runtime",
                    BundledSizeText = "~45 MB",
                    IsStaged = vulkanDetected,
                    Description = T("Included llama.cpp Vulkan engine. No separate runtime download is needed, but a chat model must still be installed and verified.", "Motore llama.cpp Vulkan incluso. Non serve un download separato del runtime, ma un modello di chat deve comunque essere installato e verificato.")
                },
                new()
                {
                    Id = BundledUvId,
                    DisplayName = "uv Package Manager",
                    Category = "Package Tooling",
                    BundledSizeText = "~15 MB",
                    IsStaged = uvDetected,
                    Description = T("Verified fast package manager bundled to provision managed Python environments on demand.", "Gestore di pacchetti veloce e verificato, incluso per predisporre ambienti Python gestiti su richiesta.")
                },
                new()
                {
                    Id = BundledAppId,
                    DisplayName = "PaRuX Desktop & Server Engine",
                    Category = "Application Core",
                    BundledSizeText = "~120 MB",
                    IsStaged = true,
                    Description = T("Windows desktop UI, authenticated loopback server, and local-first execution manager.", "Interfaccia desktop Windows, server loopback autenticato e gestore di esecuzione local-first.")
                }
            };

            // Downloaded items (only when chosen by user, with estimated sizes)
            const string modelTitle = "Compatible Chat Model";
            const long modelSizeBytes = 0;

            var downloadableList = new List<DownloadableComponentItem>
            {
                new()
                {
                    Id = DownloadableModelId,
                    DisplayName = modelTitle,
                    Category = "Model Weights (GGUF)",
                    EstimatedSizeBytes = modelSizeBytes,
                    EstimatedSizeText = "Varies by model",
                    IsRequired = true,
                    WhenDownloaded = "Download only after choosing a compatible model in Model Hub.",
                    Description = T("Weights are stored in your LocalAI models folder. Nothing downloads until you choose it.", "I pesi sono salvati nella cartella dei modelli LocalAI. Nulla viene scaricato finché non lo scegli.")
                },
                new()
                {
                    Id = DownloadableCudaId,
                    DisplayName = "NVIDIA CUDA Acceleration Pack",
                    Category = "Optional Text Runtime",
                    EstimatedSizeBytes = CudaRuntimeSizeBytes,
                    EstimatedSizeText = FormatSize(CudaRuntimeSizeBytes),
                    IsRequired = false,
                    WhenDownloaded = "Optional. Only downloaded for older NVIDIA GPUs, if you choose 'Install with PaRuX' for CUDA acceleration.",
                    Description = T("Heavy GPU acceleration for NVIDIA cards. GPUs that support it (RTX 50 series with driver 580 or newer) already get an included CUDA 13.4 engine; this pack is the CUDA 12.4 build for older cards. The bundled Vulkan runtime is sufficient for chat.", "Accelerazione GPU pesante per schede NVIDIA. Le GPU che la supportano (serie RTX 50 con driver 580 o successivo) hanno già un motore CUDA 13.4 incluso; questo pacchetto è la build CUDA 12.4 per schede più vecchie. Il runtime Vulkan incluso basta per la chat.")
                },
                new()
                {
                    Id = DownloadableComfyUiId,
                    DisplayName = "ComfyUI Media Runtime",
                    Category = "Optional Media Runtime",
                    EstimatedSizeBytes = ComfyUiSizeBytes,
                    EstimatedSizeText = FormatSize(ComfyUiSizeBytes),
                    IsRequired = false,
                    WhenDownloaded = "Optional. Only downloaded if you choose to install local image or video generation tools.",
                    Description = T("Required only for local image and video workflows. Not needed for text chat.", "Serve solo per i flussi locali di immagini e video. Non serve per la chat di testo.")
                }
            };

            bool hasWorkingTextPath = vulkanDetected || cudaDetected;
            string headline = hasWorkingTextPath
                ? "Included text engine detected — choose a compatible chat model or use one already available"
                : components == null
                    ? "Checking the included text engine and package tools…"
                    : "Text engine not detected — review Components to set it up";

            string subhead = hasWorkingTextPath
                ? (cudaDetected && !vulkanDetected
                    ? "The included CUDA 13.4 runtime is detected. Choose or use an existing chat model, then verify a real local answer before entering Chat."
                    : "The included Vulkan runtime is detected. Choose or use an existing chat model, then verify a real local answer before entering Chat.")
                : "The installer is designed to include the Vulkan engine, a CUDA 13.4 engine for supported NVIDIA GPUs, and package tooling. If detection fails, repair or locate the runtime in Components.";

            string guidance = $"Verify installer integrity: run '{PowerShellVerificationCommand}' in PowerShell and compare with SHA256SUMS.txt. See {DocumentationPath} for details.";

            return new FirstInstallEffortOverview
            {
                HasWorkingTextPathWithoutDownload = hasWorkingTextPath,
                Headline = headline,
                Subhead = subhead,
                BundledItems = bundledList,
                DownloadableItems = downloadableList,
                HashCheckGuidance = guidance,
                PowerShellHashCommand = PowerShellVerificationCommand,
                CmdHashCommand = CmdVerificationCommand,
                AuthenticodeCheckCommand = AuthenticodeVerificationCommand,
                DocReference = DocumentationPath
            };
        }

        public static bool IsComponentDetected(IReadOnlyList<ComponentStatus>? components, string componentId)
        {
            if (components == null || components.Count == 0) return false;
            return components.Any(c =>
                string.Equals(c.Id, componentId, StringComparison.OrdinalIgnoreCase) && c.Detected);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            double val = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unitIndex = 0;
            while (val >= 1024.0 && unitIndex < units.Length - 1)
            {
                val /= 1024.0;
                unitIndex++;
            }
            return unitIndex == 0
                ? $"{val:0} B"
                : $"~{val.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} {units[unitIndex]}";
        }
    }
}
