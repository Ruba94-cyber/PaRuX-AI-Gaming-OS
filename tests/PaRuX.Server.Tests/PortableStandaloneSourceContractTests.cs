using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Source-only gates for the full Standard Portable and portable-data isolation.
/// A green assertion is NOT a substitute for Windows publish, native loader,
/// real Chat/RAG acceptance or relocation to a clean low-spec PC.
/// </summary>
public sealed class PortableStandaloneSourceContractTests
{
    [Fact]
    public void Both_zip_editions_detect_a_colocated_root_without_a_batch_launcher()
    {
        string data = Read("src", "PaRuX.Contracts", "PaRuXDataRoot.cs");
        string state = Read("src", "PaRuX.Contracts", "PaRuXLocalAiStateRoot.cs");
        string desktop = Read("src", "PaRuX.App", "Services", "AppPaths.cs");
        string core = Read("src", "PaRuX.Server", "LocalAiPaths.cs");
        Assert.Contains("public const string PortableMarkerFileName", data, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(AppContext.BaseDirectory)", data, StringComparison.Ordinal);
        Assert.Contains("CorporateMode.MarkerFileName", data, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(AppContext.BaseDirectory)", state, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(AppContext.BaseDirectory)", desktop, StringComparison.Ordinal);
        Assert.Contains("FindPortableDataRoot(AppContext.BaseDirectory)", core, StringComparison.Ordinal);
        Assert.Contains("return Path.Combine(portableData, \"LocalAI\")", core, StringComparison.Ordinal);
        Assert.Contains("return Path.Combine(portableData, \"LocalAI\")", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_zip_cannot_ship_as_an_empty_chat_shell_by_default()
    {
        string build = Read("scripts", "build-portable.ps1");
        Assert.Contains("$fullyStandalone = -not $WithoutVulkan -and -not $ShellOnly", build, StringComparison.Ordinal);
        Assert.Contains("Resolve-PaRuXPortableOfflineAssets", build, StringComparison.Ordinal);
        Assert.Contains("parux.portable", build, StringComparison.Ordinal);
        Assert.Contains("PaRuXData/.portable-data", build, StringComparison.Ordinal);
        Assert.Contains(".portable-default-model", build, StringComparison.Ordinal);
        Assert.Contains("Qwen3-4B-Q4_K_M.gguf", build, StringComparison.Ordinal);
        Assert.Contains("ai-runtimes", build, StringComparison.Ordinal);
        Assert.Contains("llama-server.exe", build, StringComparison.Ordinal);
        Assert.Contains("MANIFEST-portable.json", build, StringComparison.Ordinal);
        Assert.Contains("Portable archive SHA-256 mismatch", build, StringComparison.Ordinal);
        Assert.Contains("PaRuX.Server/hostfxr.dll", build, StringComparison.Ordinal);
        Assert.Contains("PaRuX.Server/hostpolicy.dll", build, StringComparison.Ordinal);
        Assert.Contains("PaRuX.Server/coreclr.dll", build, StringComparison.Ordinal);
        Assert.Contains("-FetchAssets", build, StringComparison.Ordinal);
    }

    [Fact]
    public void No_runtime_or_model_download_is_needed_on_destination_for_offline_chat_search()
    {
        string build = Read("scripts", "build-portable.ps1");
        string assets = Read("scripts", "portable-offline-assets.ps1");
        Assert.Contains("Qwen/Qwen3-4B-GGUF", assets, StringComparison.Ordinal);
        Assert.Contains("7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5", assets, StringComparison.Ordinal);
        Assert.Contains("1110a243fdf4706b3f48f1d95db1a4f5529b4d41", assets, StringComparison.Ordinal);
        Assert.Contains("a09144355adeed5f58c8ed011d209bf8ee5a1fec", assets, StringComparison.Ordinal);
        Assert.Contains("The llama.cpp release does not publish SHA-256 metadata", assets, StringComparison.Ordinal);
        Assert.Contains("Assert-PaRuXPinnedAsset", assets, StringComparison.Ordinal);
        Assert.Contains("Assert-PaRuXNoReparseTree", assets, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $offline.ModelPath", build, StringComparison.Ordinal);
        Assert.Contains("EmbeddingDir", build, StringComparison.Ordinal);
        Assert.Contains("RerankerDir", build, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_uses_real_native_CPU_fallback_when_Vulkan_or_GPU_is_unusable()
    {
        string ai = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        string paths = Read("src", "PaRuX.Server", "LocalAiPaths.cs");
        Assert.Contains("cpuOnly && selfContainedPortable", ai, StringComparison.Ordinal);
        Assert.Contains("portable native CPU", ai, StringComparison.Ordinal);
        Assert.Contains("FindBundledPortableModel()", ai, StringComparison.Ordinal);
        Assert.Contains("FindBundledPortableModel()", paths, StringComparison.Ordinal);
        Assert.Contains("StandardVision =>", paths, StringComparison.Ordinal);
        Assert.Contains("CPU (Standard Portable)", ai, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_launches_direct_zip_executable_without_an_inherited_profile_override()
    {
        string build = Read("scripts", "build-portable.ps1");
        Assert.Contains("PARUX_CORPORATE_MODEL_DOWNLOADS", build, StringComparison.Ordinal);
        Assert.Contains("[Environment]::SetEnvironmentVariable($name, $null, \"Process\")", build, StringComparison.Ordinal);
        Assert.Contains("Join-Path $app \"PaRuXData\"", build, StringComparison.Ordinal);
        Assert.Contains("cpu-help.stdout.txt", build, StringComparison.Ordinal);
        Assert.Contains("/api/v1/embeddings/engine", build, StringComparison.Ordinal);
        Assert.Contains("/api/v1/reranker/engine", build, StringComparison.Ordinal);
        Assert.Contains("Offline Portable embedding ONNX engine did not load", build, StringComparison.Ordinal);
        Assert.Equal(1, build.Split("Write-Host \"Done.\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void The_Corporate_packager_still_uses_the_CPU_only_shell_not_the_standard_modelled_zip()
    {
        string corporate = Read("scripts", "build-corporate-lite.ps1");
        string portable = Read("scripts", "build-portable.ps1");
        Assert.Contains("-WithoutVulkan", corporate, StringComparison.Ordinal);
        Assert.Contains("if (-not $WithoutVulkan)", portable, StringComparison.Ordinal);
        Assert.Contains("$fullyStandalone = -not $WithoutVulkan -and -not $ShellOnly", portable, StringComparison.Ordinal);
        Assert.Contains("if ($WithoutVulkan -and $FetchAssets)", portable, StringComparison.Ordinal);
    }

    private static string Read(params string[] relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(relative).ToArray()));
    }
}
