using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Corporate Lite Core wiring found missing by the measurement run: the bundled CPU runtime was never resolved and the
/// Core held connections to the internet. Synthetic folders only.</summary>
public sealed class CorporateLiteCoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corp-core-" + Guid.NewGuid().ToString("N"));

    public CorporateLiteCoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Runtime(string folder, bool withExe = true)
    {
        string dir = Path.Combine(_root, "runtime", folder);
        Directory.CreateDirectory(dir);
        if (withExe) File.WriteAllText(Path.Combine(dir, LocalAiPaths.LlamaServerExecutable), "x");
        return dir;
    }

    [Fact]
    public void Newest_cpu_runtime_with_an_executable_is_chosen()
    {
        Runtime("llama.cpp-b9000-cpu");
        string newest = Runtime("llama.cpp-b12119-cpu");
        Runtime("llama.cpp-b13000-cpu", withExe: false);
        Runtime("llama.cpp-b14000-vulkan");
        Assert.Equal(newest, LocalAiPaths.FindBundledCpuRuntime(Path.Combine(_root, "runtime")));
    }

    [Fact]
    public void No_cpu_runtime_gives_null()
    {
        Runtime("llama.cpp-b14000-vulkan");
        Assert.Null(LocalAiPaths.FindBundledCpuRuntime(Path.Combine(_root, "runtime")));
        Assert.Null(LocalAiPaths.FindBundledCpuRuntime(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Bundled_model_is_the_text_gguf_never_a_projector()
    {
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "mmproj-small.gguf"), "x");
        File.WriteAllText(Path.Combine(models, "notes.txt"), "x");
        Assert.Null(LocalAiPaths.FindBundledCorporateModel(models));
        string model = Path.Combine(models, "Tiny-1B-Q8_0.gguf");
        File.WriteAllText(model, "x");
        Assert.Equal(model, LocalAiPaths.FindBundledCorporateModel(models));
    }

    [Fact]
    public void Corporate_model_downloads_are_opt_in_and_never_enable_standard_installations()
    {
        Assert.False(CorporateMode.AreModelDownloadsAllowed(true, null));
        Assert.False(CorporateMode.AreModelDownloadsAllowed(true, "0"));
        Assert.False(CorporateMode.AreModelDownloadsAllowed(false, "1"));
        Assert.True(CorporateMode.AreModelDownloadsAllowed(true, "1"));
        Assert.True(CorporateMode.AreModelDownloadsAllowed(true, " TRUE "));
    }

    [Fact]
    public void Corporate_model_import_refuses_projectors_empty_and_unsafe_paths()
    {
        Assert.True(CorporateModelImportPolicy.IsAcceptableModel("qwen-Q4_K_M.gguf", 200_000));
        Assert.False(CorporateModelImportPolicy.IsAcceptableModel("empty.gguf", 1));
        Assert.False(CorporateModelImportPolicy.IsAcceptableModel("mmproj.gguf", 200_000));
        Assert.False(CorporateModelImportPolicy.IsAcceptableModel("vision-projector.gguf", 200_000));
        Assert.False(CorporateModelImportPolicy.IsAcceptableModel("../out.gguf", 200_000));
        Assert.False(CorporateModelImportPolicy.IsAcceptableModel("model.onnx", 200_000));
    }

    [Fact]
    public void Corporate_gguf_header_is_checked_without_loading_a_multi_gigabyte_file()
    {
        string path = Path.Combine(_root, "synthetic-gguf.gguf");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            fs.SetLength(128_000);
            fs.Write(new byte[] { (byte)'G', (byte)'G', (byte)'U', (byte)'F' });
        }
        Assert.True(CorporateModelImportPolicy.IsAcceptableModel(Path.GetFileName(path), new FileInfo(path).Length));
        Assert.True(CorporateModelImportPolicy.HasGgufHeader(path));
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            fs.Write(new byte[] { (byte)'B', (byte)'A', (byte)'D', (byte)'!' });
        Assert.False(CorporateModelImportPolicy.HasGgufHeader(path));
        Assert.False(CorporateModelImportPolicy.HasGgufHeader(Path.Combine(_root, "missing.gguf")));
    }

    [Fact]
    public void Corporate_package_default_is_stable_when_new_models_are_added()
    {
        string models = Path.Combine(_root, "corporate-models");
        Directory.CreateDirectory(models);
        string fallback = Path.Combine(models, "z-original.gguf");
        File.WriteAllText(fallback, "synthetic");
        File.WriteAllText(Path.Combine(models, "a-new.gguf"), "synthetic");
        File.WriteAllText(Path.Combine(models, ".corporate-default-model"), "z-original.gguf");
        Assert.Equal(fallback, LocalAiPaths.FindBundledCorporateModel(models));
        File.WriteAllText(Path.Combine(models, ".corporate-default-model"), "../outside.gguf");
        Assert.Equal(Path.Combine(models, "a-new.gguf"), LocalAiPaths.FindBundledCorporateModel(models));
    }

    [Fact]
    public void Corporate_download_policy_accepts_only_gguf_and_resumes_saved_gguf()
    {
        Assert.True(CorporateModelDownloadPolicy.IsTextGgufRequest(
            new ModelDownloadRequest { FileName = "model-Q4_K_M.gguf" }));
        Assert.False(CorporateModelDownloadPolicy.IsTextGgufRequest(
            new ModelDownloadRequest { FileName = "image.safetensors" }));
        Assert.False(CorporateModelDownloadPolicy.IsTextGgufRequest(
            new ModelDownloadRequest { FileName = "mmproj.gguf" }));
        Assert.False(CorporateModelDownloadPolicy.IsTextGgufRequest(
            new ModelDownloadRequest { FileName = "model.gguf", MediaCheckpointType = "video" }));
        Assert.True(CorporateModelDownloadPolicy.IsTextGgufRequest(
            new ModelDownloadRequest { ResumeFromDownloadId = "job-1" },
            id => id == "job-1" ? "resume.gguf" : null));
        Assert.False(CorporateModelDownloadPolicy.IsTextGgufRequest(
            new ModelDownloadRequest { ResumeFromDownloadId = "job-2" },
            _ => "bad.safetensors"));
    }

    [Fact]
    public void Corporate_saved_chat_model_recovers_to_starter_only_when_preference_is_unavailable()
    {
        Func<string, bool> usable = path => path is "starter.gguf" or "chat.gguf";

        var preferred = CorporateTextModelChoicePolicy.Resolve(
            null, "chat.gguf", "starter.gguf", usable);
        Assert.Equal("chat.gguf", preferred.Path);
        Assert.False(preferred.RecoveredFromSavedChoice);

        var recovered = CorporateTextModelChoicePolicy.Resolve(
            null, "missing.gguf", "starter.gguf", usable);
        Assert.Equal("starter.gguf", recovered.Path);
        Assert.True(recovered.RecoveredFromSavedChoice);

        // An explicitly selected but invalid model is NOT silently replaced.
        var explicitMissing = CorporateTextModelChoicePolicy.Resolve(
            "requested.gguf", "chat.gguf", "starter.gguf", usable);
        Assert.Equal("requested.gguf", explicitMissing.Path);
        Assert.False(explicitMissing.RecoveredFromSavedChoice);

        // If the starter is missing too, report the actual missing role rather
        // than claiming recovery succeeded.
        var noFallback = CorporateTextModelChoicePolicy.Resolve(
            null, "missing.gguf", "missing-starter.gguf", usable);
        Assert.Equal("missing.gguf", noFallback.Path);
        Assert.False(noFallback.RecoveredFromSavedChoice);
        Assert.Equal("starter.gguf",
            CorporateTextModelChoicePolicy.Resolve(null, null, "starter.gguf", usable).Path);
    }

    [Fact]
    public void Cpu_launch_is_loopback_only_bounded_and_unloads_when_idle()
    {
        List<string> a = LocalAiProcessManager.BuildCorporateCpuArguments(@"C:\m\x.gguf", 8123, 16);
        Assert.Equal("127.0.0.1", a[a.IndexOf("--host") + 1]);
        Assert.Equal("8123", a[a.IndexOf("--port") + 1]);
        Assert.Equal("8", a[a.IndexOf("--threads") + 1]);
        Assert.Equal("2", LocalAiProcessManager.BuildCorporateCpuArguments("m", 1, 1)[LocalAiProcessManager.BuildCorporateCpuArguments("m", 1, 1).IndexOf("--threads") + 1]);
        Assert.Equal(LocalAiProcessManager.CorporateIdleUnloadSeconds.ToString(), a[a.IndexOf("--sleep-idle-seconds") + 1]);
        Assert.DoesNotContain("--device", a);
        Assert.DoesNotContain("-ngl", a);
        Assert.Contains("--no-webui", a);
    }

    [Fact]
    public void Network_guard_points_proxies_at_a_dead_loopback_port_and_keeps_loopback_direct()
    {
        Assert.StartsWith("http://127.0.0.1:", CorporateNetworkGuard.Variables["HTTPS_PROXY"]);
        Assert.Contains("127.0.0.1", CorporateNetworkGuard.Variables["NO_PROXY"]);
        Assert.Contains("localhost", CorporateNetworkGuard.Variables["NO_PROXY"]);
    }
}
