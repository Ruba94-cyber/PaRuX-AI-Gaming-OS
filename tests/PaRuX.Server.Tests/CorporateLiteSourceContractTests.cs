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
