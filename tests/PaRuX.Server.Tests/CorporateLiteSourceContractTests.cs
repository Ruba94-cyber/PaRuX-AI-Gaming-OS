using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Source/packaging contracts for the Corporate-only UX. A source contract is
/// not proof of a functioning Windows ZIP; that requires the separate native smoke/real chat.</summary>
public sealed class CorporateLiteSourceContractTests
{
    [Fact]
    public void Packaging_contains_cpu_engine_a_starter_and_two_unambiguous_launcher_modes()
    {
        string script = Read("scripts", "build-corporate-lite.ps1");
        Assert.Contains("build-portable.ps1", script, StringComparison.Ordinal);
        Assert.Contains("-WithoutVulkan", script, StringComparison.Ordinal);
        Assert.Contains("llama-server.exe", script, StringComparison.Ordinal);
        Assert.Contains(".corporate-default-model", script, StringComparison.Ordinal);
        Assert.Contains("Start PaRuX Corporate Lite.cmd", script, StringComparison.Ordinal);
        Assert.Contains("Start PaRuX Corporate Model Downloads.cmd", script, StringComparison.Ordinal);
        Assert.Contains("PARUX_CORPORATE_MODEL_DOWNLOADS=0", script, StringComparison.Ordinal);
        Assert.Contains("PARUX_CORPORATE_MODEL_DOWNLOADS=1", script, StringComparison.Ordinal);
        Assert.Contains("nativeRuntimeSha256", script, StringComparison.Ordinal);
        Assert.Contains("Starter GGUF", script, StringComparison.Ordinal);
        Assert.Contains("Offline smoke: native CPU loader", script, StringComparison.Ordinal);
        Assert.Contains("RerankerDir", script, StringComparison.Ordinal);
        Assert.Contains("RequireReranker", script, StringComparison.Ordinal);
        Assert.Contains("PaRuXData/ai-runtimes/embeddings/", script, StringComparison.Ordinal);
        Assert.Contains("PaRuXData/ai-runtimes/reranker/", script, StringComparison.Ordinal);
        Assert.Contains("Corporate archive SHA-256 mismatch", script, StringComparison.Ordinal);
        Assert.Contains("NoModel edition has NO chat model", script, StringComparison.Ordinal);
        Assert.Contains("Assert-PinnedAsset", script, StringComparison.Ordinal);
        Assert.Contains("Get-PinnedAsset", script, StringComparison.Ordinal);
        Assert.Contains("llama-cpu-", script, StringComparison.Ordinal);
        Assert.Contains("Untrusted llama.cpp release tag", script, StringComparison.Ordinal);
        Assert.Contains("Cached managed model asset is invalid", script, StringComparison.Ordinal);
        Assert.Contains("/api/v1/embeddings/engine", script, StringComparison.Ordinal);
        Assert.Contains("/api/v1/reranker/engine", script, StringComparison.Ordinal);
        Assert.Contains("bundled embedding ONNX engine failed real native load", script, StringComparison.Ordinal);
        Assert.Equal(1, script.Split("# --- 3. Contained, pre-seeded data root", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, script.Split("Write-Host \"Done.\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Corporate_model_hub_exposes_real_import_and_chat_selection_controls()
    {
        string corporateView = Read("src", "PaRuX.App", "Views", "ModelHubView.Corporate.cs");
        string start = Read("src", "PaRuX.App", "Views", "ModelHubView.Start.cs");
        string tabs = Read("src", "PaRuX.App", "Views", "ModelHubView.Tabs.cs");

        Assert.Contains("ModelHub_Corporate_ImportGguf_Button", corporateView, StringComparison.Ordinal);
        Assert.Contains("ImportManagedModelAsync", corporateView, StringComparison.Ordinal);
        Assert.Contains("ModelRolesClient.Current.SetRoleAsync(\"standard\"", corporateView, StringComparison.Ordinal);
        Assert.Contains("ExternalModelImportPicker.ShowAsync", corporateView, StringComparison.Ordinal);
        Assert.Contains("CorporateMode.ModelDownloadsAllowed", corporateView, StringComparison.Ordinal);
        Assert.Contains("panel.Children.Add(BuildCorporateModelCard());", start, StringComparison.Ordinal);
        Assert.Contains("if (CorporateMode.IsActive && tab == ModelHubTab.Media) continue;", tabs, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_server_still_blocks_runtime_gaming_media_and_unapproved_downloads()
    {
        string server = Read("src", "PaRuX.Server", "Program.cs");
        string transfer = Read("src", "PaRuX.Server", "ModelDownloadService.cs");
        string mode = Read("src", "PaRuX.Contracts", "CorporateMode.cs");

        Assert.Contains("path.StartsWith(\"/api/v1/runtime/install\"", server, StringComparison.Ordinal);
        Assert.Contains("path.StartsWith(\"/api/v1/gaming/\"", server, StringComparison.Ordinal);
        Assert.Contains("path.StartsWith(\"/api/v1/media/\"", server, StringComparison.Ordinal);
        Assert.Contains("CorporateMode.ModelDownloadsAllowed", server, StringComparison.Ordinal);
        Assert.Contains("CorporateModelDownloadPolicy.IsTextGgufRequest", server, StringComparison.Ordinal);
        Assert.Contains("CorporateModelDownloadPolicy.IsTextGgufFileName", transfer, StringComparison.Ordinal);
        Assert.Contains("public const string ModelDownloadsEnvironmentVariable", mode, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_benchmark_has_an_explicit_fail_closed_acceptance_mode()
    {
        string benchmark = Read("tests", "corporate", "measure-corporate-lite.ps1");
        Assert.Contains("[switch]$EnforceAcceptance", benchmark, StringComparison.Ordinal);
        Assert.Contains("Complete Corporate acceptance requires a bundled text GGUF", benchmark, StringComparison.Ordinal);
        Assert.Contains("search_exact_id_exact_mode.hits", benchmark, StringComparison.Ordinal);
        Assert.Contains("chat.answer_mentions_expected_source", benchmark, StringComparison.Ordinal);
        Assert.Contains("nonloopback_outbound_connections", benchmark, StringComparison.Ordinal);
        Assert.Contains("Corporate offline acceptance FAILED", benchmark, StringComparison.Ordinal);
        Assert.Contains("/api/v1/embeddings/engine", benchmark, StringComparison.Ordinal);
        Assert.Contains("/api/v1/reranker/engine", benchmark, StringComparison.Ordinal);
        Assert.Contains("Real ONNX embedding/reranker loader did not succeed", benchmark, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_offline_manifest_pins_match_the_actual_core_retrieval_engine_contract()
    {
        string build = Read("scripts", "build-corporate-lite.ps1");
        string source = Read("src", "PaRuX.Server", "Knowledge", "KnowledgeSecurity.cs");
        foreach (string pinned in new[] {
            "1110a243fdf4706b3f48f1d95db1a4f5529b4d41",
            "a09144355adeed5f58c8ed011d209bf8ee5a1fec",
            "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452",
            "c623d0bcb99f4622beb413eaef00cfbe5db20df9f1dd982da4b4f26022881870",
            "fb140275c155a9c7c5a3b3e0e77a9e839594a938",
            "c79f2b6a0cea6f4b564fed1938984bace9d30ff0",
            "72b987fd805cfa2b58c4c8c952b274a11bfd5a00"
        })
        {
            Assert.Contains(pinned, source, StringComparison.Ordinal);
            Assert.Contains(pinned, build, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Corporate_model_hub_reports_offline_components_without_claiming_live_model_inference()
    {
        string ui = Read("src", "PaRuX.App", "Views", "ModelHubView.Corporate.cs");
        string contract = Read("src", "PaRuX.Contracts", "CorporateOfflineReadiness.cs");
        Assert.Contains("ModelHub_Corporate_OfflineReadiness_Status", ui, StringComparison.Ordinal);
        Assert.Contains("ModelHub_Corporate_OfflineReadiness_Recheck", ui, StringComparison.Ordinal);
        Assert.Contains("CorporateOfflineReadiness.Inspect(PaRuXDataRoot.Path)", ui, StringComparison.Ordinal);
        Assert.Contains("not a functional test", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", contract, StringComparison.Ordinal);
    }

    private static string Read(params string[] path)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray()));
    }
}
