using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-M1 (llama.cpp CPU archive pin), SEC-M2 (Core-owned bundled starter decision) and
/// SEC-M3 (RAM admission for the CPU fallback). Synthetic data only.</summary>
public sealed class SecurityMediumM123Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-secm123-" + Guid.NewGuid().ToString("N"));
    public SecurityMediumM123Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // ---- SEC-M1 -------------------------------------------------------------------------------

    [Fact]
    public void Llama_cpu_lock_file_has_wellformed_unique_pins_matching_archive_names()
    {
        using JsonDocument doc = JsonDocument.Parse(Read("packaging", "llama-cpu-archive.lock.json"));
        var pins = doc.RootElement.GetProperty("pins").EnumerateArray().ToList();
        Assert.NotEmpty(pins);
        foreach (JsonElement pin in pins)
        {
            string tag = pin.GetProperty("tag").GetString()!;
            Assert.Matches("^[A-Za-z0-9._-]+$", tag);
            Assert.Equal("llama-" + tag + "-bin-win-cpu-x64.zip", pin.GetProperty("archiveName").GetString());
            Assert.Matches("^[0-9a-f]{64}$", pin.GetProperty("sha256").GetString()!);
        }
        Assert.Equal(pins.Count, pins.Select(p => p.GetProperty("tag").GetString()).Distinct().Count());
    }

    [Fact]
    public void Builders_fail_closed_on_unpinned_tags_and_check_the_archive_against_the_pin()
    {
        string pin = Read("scripts", "llama-cpu-pin.ps1");
        Assert.Contains("No reviewed SHA-256 pin", pin, StringComparison.Ordinal);
        Assert.Contains("does not match the reviewed pin", pin, StringComparison.Ordinal);
        Assert.Contains("Upstream release digest differs from the reviewed pin", pin, StringComparison.Ordinal);
        foreach (string script in new[] { "portable-offline-assets.ps1", "build-corporate-lite.ps1" })
        {
            string text = Read("scripts", script);
            Assert.Contains("Get-PaRuXLlamaCpuPin", text, StringComparison.Ordinal);
            Assert.Contains("AllowUnpinnedLlamaCpuRuntime", text, StringComparison.Ordinal);
            Assert.Contains("Assert-PaRuXLlamaCpuArchivePin", text, StringComparison.Ordinal);
        }
        Assert.Contains("AllowUnpinnedLlamaCpuRuntime", Read("scripts", "build-portable.ps1"), StringComparison.Ordinal);
    }

    // ---- SEC-M2 -------------------------------------------------------------------------------

    private string MakeStarterDir()
    {
        string dir = Path.Combine(_root, "models");
        Directory.CreateDirectory(dir);
        using (var fs = new FileStream(Path.Combine(dir, "starter.gguf"), FileMode.CreateNew, FileAccess.Write))
        {
            fs.SetLength(70_000);
            fs.Write(new byte[] { (byte)'G', (byte)'G', (byte)'U', (byte)'F' });
        }
        File.WriteAllText(Path.Combine(dir, ".portable-default-model"), "starter.gguf");
        return dir;
    }

    [Fact]
    public void Core_reports_the_starter_only_for_a_valid_non_corporate_portable_layout()
    {
        string dir = MakeStarterDir();
        Assert.Equal("starter.gguf", LocalAiPaths.GetBundledPortableStarterFileName(false, true, dir));
        Assert.Null(LocalAiPaths.GetBundledPortableStarterFileName(true, true, dir));
        Assert.Null(LocalAiPaths.GetBundledPortableStarterFileName(false, false, dir));
        File.Delete(Path.Combine(dir, ".portable-default-model"));
        Assert.Null(LocalAiPaths.GetBundledPortableStarterFileName(false, true, dir));
    }

    [Fact]
    public void Onboarding_consumes_the_core_decision_and_never_probes_the_file_system_itself()
    {
        string ui = Read("src", "PaRuX.App", "Views", "OnboardingView.FirstModel.cs");
        Assert.Contains("BundledPortableStarterModel", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("Qwen3-4B-Q4_K_M.gguf", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("HasGgufHeader", ui, StringComparison.Ordinal);
        Assert.Contains("BundledPortableStarterModel", Read("src", "PaRuX.Contracts", "ApiContracts.cs"), StringComparison.Ordinal);
        Assert.Contains("GetBundledPortableStarterFileName", Read("src", "PaRuX.Server", "Program.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Advice_without_core_confirmation_means_not_included()
    {
        Assert.Null(new FirstModelAdvice().BundledPortableStarterModel);
    }

    // ---- SEC-M3 -------------------------------------------------------------------------------

    private const ulong Gib = 1UL << 30;

    [Fact]
    public void Cpu_fallback_is_refused_with_a_readable_reason_when_the_model_cannot_fit()
    {
        var ram = SystemRamSnapshot.TryCreate(16 * Gib, 3 * Gib);
        string? refusal = CpuFallbackAdmission.Evaluate(ram, 85, 0, neededMib: 6 * 1024);
        Assert.NotNull(refusal);
        Assert.Contains("Not enough free memory for CPU mode", refusal, StringComparison.Ordinal);
        Assert.Contains("No other program was stopped", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Cpu_fallback_is_admitted_when_it_fits_or_when_ram_cannot_be_measured()
    {
        Assert.Null(CpuFallbackAdmission.Evaluate(SystemRamSnapshot.TryCreate(16 * Gib, 12 * Gib), 85, 0, neededMib: 4 * 1024));
        Assert.Null(CpuFallbackAdmission.Evaluate(null, 85, 0, neededMib: 64 * 1024));
    }

    [Fact]
    public void Cpu_fallback_respects_the_profile_ceiling_not_only_free_memory()
    {
        // 32 GiB total, 28 GiB free, but the ceiling is 50% and 20 GiB are used by others: headroom is negative.
        var ram = SystemRamSnapshot.TryCreate(32 * Gib, 12 * Gib);
        Assert.NotNull(CpuFallbackAdmission.Evaluate(ram, 50, 0, neededMib: 2 * 1024));
    }

    [Fact]
    public void Cpu_start_path_runs_admission_before_launching_and_does_not_kill_other_processes()
    {
        string ai = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        int admission = ai.IndexOf("CpuFallbackAdmission.Check", StringComparison.Ordinal);
        int launch = ai.IndexOf("BuildCorporateCpuArguments(model, TextPort", StringComparison.Ordinal);
        Assert.True(admission > 0 && launch > admission);
        string guard = Read("src", "PaRuX.Server", "CpuFallbackAdmission.cs");
        Assert.DoesNotContain(".Kill(", guard, StringComparison.Ordinal);
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
