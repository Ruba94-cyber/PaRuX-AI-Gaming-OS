using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Follow-up defects of the real-app engine install run: the 260 character linker limit under a deep data root, build
/// requirements outside the hash lock, uv output decoding, a misleading Diffusers status, the gateway port race and
/// package versions that differed between two locks installed into one venv.
/// </summary>
public sealed class ManagedEngineFollowUpTests
{
    // ---- Defect 1: deep data root ----

    // The failing link output of the real run, measured: cache root 100 chars -> .pyd path 265 chars (LNK1104).
    private const string SdistSuffix = @"\sdists-v9\pypi\insightface\0.7.3\CjEj5jvs8Fb51jnJ\src";
    private const string DeepestLinkerPath =
        @"\build\temp.win-amd64-cpython-311\Release\insightface\thirdparty\face3d\mesh\cython\mesh_core_cython.cp311-win_amd64.lib";

    [Fact]
    public void The_budget_matches_the_measured_longest_insightface_build_path()
    {
        Assert.Equal(ManagedUvCachePlan.KnownBuildSuffixLength, (SdistSuffix + DeepestLinkerPath).Length);
        Assert.True(ManagedUvCachePlan.MaxCacheRootLength + ManagedUvCachePlan.KnownBuildSuffixLength < 260);
        string realFailingCache = @"C:\Users\runner\Desktop\PaRuX\.ai\artifacts\realapp-20261114\engines-data\ai-runtimes\toolchain\cache";
        Assert.True((realFailingCache + SdistSuffix + DeepestLinkerPath).Length >= 260);
        Assert.True(realFailingCache.Length > ManagedUvCachePlan.MaxCacheRootLength);
    }

    // The Python folder follows the product layout: <data root>\ai-runtimes\toolchain\python.
    private static ManagedUvCachePlan.Plan PlanFor(string cache, string dataRoot, string? portableRoot, string? localAppData) =>
        ManagedUvCachePlan.Resolve(cache, dataRoot + @"\ai-runtimes\toolchain\python", dataRoot, portableRoot, localAppData);

    [Fact]
    public void A_short_cache_with_a_python_folder_too_deep_for_the_compiler_is_still_relocated()
    {
        // Real run: cl.exe C1083 on Include\cpython\bytearrayobject.h (260 characters) under a 164-character data root.
        string dataRoot = @"C:\" + new string('d', 150);
        string python = dataRoot + @"\ai-runtimes\toolchain\python";
        Assert.True(python.Length > ManagedUvCachePlan.MaxPythonRootLength);
        ManagedUvCachePlan.Plan plan = ManagedUvCachePlan.Resolve(@"C:\short\cache", python, dataRoot, null, @"C:\Users\a\AppData\Local");
        Assert.True(plan.Relocated);
        Assert.StartsWith(@"C:\Users\a\AppData\Local\PaRuX\uvp\", plan.PythonRoot);
        Assert.True(plan.PythonRoot.Length <= ManagedUvCachePlan.MaxPythonRootLength);
    }

    [Fact]
    public void A_venv_too_deep_to_load_its_own_dlls_is_refused_before_any_download()
    {
        // Measured: onnxruntime_pybind11_state.pyd failed to load ("Filename or extension too long") in an avatar venv under a 164-character data root.
        string deepVenv = @"C:\" + new string('d', 161) + @"\ai-runtimes\avatar\LatentSync\.venv";
        Assert.True(deepVenv.Length > ManagedUvCachePlan.MaxVenvRootLength);
        var ex = Assert.Throws<ManagedDataPathTooLongException>(() => ManagedUvCachePlan.RequireVenvPathBudget(deepVenv));
        Assert.Equal("data_path_too_long", ManagedInstallFailureCode.Of(ex));
        // The real 100-character data root of the first run still fits.
        ManagedUvCachePlan.RequireVenvPathBudget(@"C:\Users\runner\Desktop\PaRuX\.ai\artifacts\realapp-20261114\engines-data\ai-runtimes\avatar\LatentSync\.venv");
    }

    [Fact]
    public void A_short_data_root_keeps_its_own_cache()
    {
        ManagedUvCachePlan.Plan plan = PlanFor(@"C:\pxk\ai-runtimes\toolchain\cache", @"C:\pxk", null, @"C:\Users\a\AppData\Local");
        Assert.Equal(@"C:\pxk\ai-runtimes\toolchain\cache", plan.CacheRoot);
        Assert.True(plan.WithinBudget);
        Assert.False(plan.Relocated);
    }

    [Fact]
    public void A_deep_data_root_gets_a_short_per_data_root_cache_under_the_user_profile()
    {
        string dataRoot = @"C:\Users\runner\Desktop\PaRuX\.ai\artifacts\realapp-20261114\engines-data";
        string deepCache = dataRoot + @"\ai-runtimes\toolchain\cache";
        ManagedUvCachePlan.Plan plan = PlanFor(deepCache, dataRoot, null, @"C:\Users\runner\AppData\Local");
        Assert.True(plan.Relocated);
        Assert.True(plan.WithinBudget);
        Assert.True(plan.CacheRoot.Length <= ManagedUvCachePlan.MaxCacheRootLength);
        Assert.StartsWith(@"C:\Users\runner\AppData\Local\PaRuX\uvc\", plan.CacheRoot);
        Assert.True((plan.CacheRoot + SdistSuffix + DeepestLinkerPath).Length < 260);
        // Stable per data root, different between two data roots.
        Assert.Equal(plan.CacheRoot, PlanFor(deepCache, dataRoot.ToUpperInvariant() + @"\", null, @"C:\Users\runner\AppData\Local").CacheRoot);
        Assert.NotEqual(plan.CacheRoot, PlanFor(deepCache, dataRoot + "2", null, @"C:\Users\runner\AppData\Local").CacheRoot);
    }

    [Fact]
    public void A_portable_data_root_never_writes_outside_itself_and_is_flagged_over_budget()
    {
        string portable = @"D:\Some\Very\Deep\Folder\Structure\For\A\Usb\Stick\With\Many\Nested\Directories\Inside\PaRuX-portable";
        string dataRoot = portable + @"\PaRuXData";
        ManagedUvCachePlan.Plan plan = PlanFor(dataRoot + @"\ai-runtimes\toolchain\cache", dataRoot, dataRoot, @"C:\Users\runner\AppData\Local");
        Assert.False(plan.Relocated);
        Assert.False(plan.WithinBudget);
        Assert.StartsWith(dataRoot, plan.CacheRoot);
    }

    [Fact]
    public void No_usable_user_profile_folder_means_over_budget_not_a_guess()
    {
        string dataRoot = new string('x', 120);
        Assert.False(PlanFor(@"C:\" + dataRoot + @"\cache", @"C:\" + dataRoot, null, null).WithinBudget);
        string longProfile = @"C:\Users\" + new string('u', 90) + @"\AppData\Local";
        Assert.False(PlanFor(@"C:\" + dataRoot + @"\cache", @"C:\" + dataRoot, null, longProfile).WithinBudget);
    }

    [Theory]
    [InlineData("LINK : fatal error LNK1104: impossibile aprire il file 'build\\lib...pyd'", true)]
    [InlineData("OSError: [WinError 206] The filename or extension is too long", true)]
    [InlineData("error: Unable to find a compatible Visual Studio installation.", false)]
    [InlineData("", false)]
    public void Path_limit_failures_are_recognised_from_the_linker_and_python_logs(string log, bool expected) =>
        Assert.Equal(expected, ManagedUvCachePlan.LooksLikePathLimitFailure(log));

    [Fact]
    public void The_typed_failure_has_a_stable_code_and_is_not_confused_with_missing_build_tools()
    {
        var tooLong = new ManagedDataPathTooLongException("LNK1104");
        Assert.Equal("data_path_too_long", ManagedInstallFailureCode.Of(tooLong));
        Assert.Equal("build_tools_required", ManagedInstallFailureCode.Of(new ManagedBuildToolsRequiredException("insightface", "x")));
        Assert.Null(ManagedInstallFailureCode.Of(new InvalidOperationException("other")));
        Assert.Contains("shorter folder", tooLong.Message);
    }

    // ---- Defect 3: uv output encoding ----

    [Fact]
    public void The_uv_child_is_read_as_utf8_and_asked_for_plain_output()
    {
        var info = ManagedPythonRuntime.CreateUvStartInfo("uv.exe", new[] { "pip", "install" }, Path.GetTempPath());
        Assert.Equal(System.Text.Encoding.UTF8.CodePage, info.StandardOutputEncoding!.CodePage);
        Assert.Equal(System.Text.Encoding.UTF8.CodePage, info.StandardErrorEncoding!.CodePage);
        Assert.Equal("1", info.Environment["NO_COLOR"]);
        Assert.Equal("1", info.Environment["UV_NO_PROGRESS"]);
    }

    [Fact]
    public void Utf8_box_drawing_decoded_with_a_legacy_code_page_is_the_reported_mojibake()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("\u2570\u2500\u25B6 Failed");
        Assert.Equal("\u2570\u2500\u25B6 Failed", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.NotEqual("\u2570\u2500\u25B6 Failed", System.Text.Encoding.GetEncoding(437).GetString(bytes));
    }

    [Fact]
    public void Build_tools_detection_still_works_on_plain_utf8_uv_output()
    {
        string log = "  \u00D7 Failed to build `insightface==0.7.3`\n  \u2570\u2500\u25B6 Call to `setuptools.build_meta.build_wheel` failed\n      error: Unable to find a compatible Visual Studio installation.\n";
        Assert.True(ManagedBuildToolsFailure.IsMissingToolchain(log, out string package));
        Assert.Equal("insightface", package);
    }

    // ---- Defect 4: Diffusers status ----

    [Fact]
    public void An_installed_runtime_that_was_refused_for_cuda_does_not_say_install_the_engine()
    {
        (string code, string detail) = ManagedDiffusersImageEngine.DescribeNotReady(
            runtimeInstalled: true, modelInstalled: false, manifestDetail: null,
            lastInstallError: "PaRuX Diffusers engine failed: RuntimeError: CUDA is not available to PaRuX-managed PyTorch");
        Assert.Equal("cuda_unavailable", code);
        Assert.DoesNotContain("Install the managed engine", detail);
        Assert.Contains("CUDA", detail);
        Assert.Contains("runtime is installed", detail);
    }

    [Fact]
    public void The_not_ready_reasons_are_distinct_and_honest()
    {
        Assert.Equal("runtime_missing", ManagedDiffusersImageEngine.DescribeNotReady(false, false, null, null).Code);
        Assert.Equal("model_missing", ManagedDiffusersImageEngine.DescribeNotReady(true, false, null, null).Code);
        Assert.Equal("install_failed", ManagedDiffusersImageEngine.DescribeNotReady(true, true, "manifest changed", null).Code);
        Assert.Equal("install_failed", ManagedDiffusersImageEngine.DescribeNotReady(true, true, null, null).Code);
    }

    // ---- Defects 2 and 6: lock closure and hashed build requirements ----

    private static readonly Regex PinLine = new(@"^(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)==(?<version>\S+)", RegexOptions.Multiline);

    private static Dictionary<string, string> Pins(string set)
    {
        string text = File.ReadAllText(ManagedRequirementLocks.PathFor(set));
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in PinLine.Matches(text)) pins[m.Groups["name"].Value.Replace('_', '-')] = m.Groups["version"].Value;
        return pins;
    }

    public static IEnumerable<object[]> TorchVenvPairs()
    {
        // Every engine venv that installs a torch closure and then its own lock (see the services' install order).
        foreach (string torch in new[] { "torch-cpu", "torch-cu128" })
            foreach (string engine in new[] { "speech", "lora-convert", "threed", "diffusers-engine", "avatar" })
                yield return new object[] { torch, engine };
    }

    [Theory]
    [MemberData(nameof(TorchVenvPairs))]
    public void Two_locks_installed_into_the_same_venv_never_disagree_on_a_package_version(string torchLock, string engineLock)
    {
        Dictionary<string, string> torch = Pins(torchLock);
        Dictionary<string, string> engine = Pins(engineLock);
        var disagreements = engine.Where(e => !e.Key.Equals("torch", StringComparison.OrdinalIgnoreCase)
                && torch.TryGetValue(e.Key, out string? other) && other != e.Value)
            .Select(e => e.Key + " " + torch[e.Key] + " (" + torchLock + ") vs " + e.Value + " (" + engineLock + ")")
            .ToList();
        Assert.True(disagreements.Count == 0, "The second install would replace packages of the first:\n" + string.Join("\n", disagreements));
    }

    [Fact]
    public void The_two_torch_channels_agree_on_every_non_torch_package()
    {
        Dictionary<string, string> cpu = Pins("torch-cpu");
        Dictionary<string, string> cuda = Pins("torch-cu128");
        foreach ((string name, string version) in cpu.Where(p => !p.Key.Equals("torch", StringComparison.OrdinalIgnoreCase)))
            Assert.Equal(version, cuda[name]);
    }

    [Theory]
    [InlineData("cutout")]
    [InlineData("lora-convert")]
    [InlineData("threed")]
    [InlineData("diffusers-engine")]
    [InlineData("speech")]
    [InlineData("avatar")]
    public void Every_engine_install_passes_the_shipped_hashed_build_constraints(string set)
    {
        IReadOnlyList<string> args = ManagedRequirementLocks.ArgumentsFor(set);
        int at = args.ToList().IndexOf("--build-constraints");
        Assert.True(at > 0);
        Assert.Equal(ManagedRequirementLocks.BuildConstraintsPath, args[at + 1]);
        Assert.True(ManagedRequirementLocks.Validate(args[at + 1]) > 0);
        Assert.Equal(args, ManagedPipInstallPolicy.Evaluate(args));
    }

    [Fact]
    public void Build_constraints_cover_the_declared_build_system_of_insightface_and_match_the_avatar_lock()
    {
        // insightface 0.7.3 pyproject.toml: requires = ["setuptools", "numpy", "cython"]
        Dictionary<string, string> build = Pins(ManagedRequirementLocks.BuildConstraintsSetId);
        Dictionary<string, string> avatar = Pins("avatar");
        foreach (string name in new[] { "setuptools", "numpy", "cython", "wheel" }) Assert.True(build.ContainsKey(name), name);
        foreach (string name in new[] { "setuptools", "numpy", "cython" }) Assert.Equal(avatar[name], build[name]);
    }

    [Fact]
    public void A_foreign_build_constraints_file_is_refused()
    {
        string other = Path.Combine(Path.GetTempPath(), "evil.lock.txt");
        string lockPath = ManagedRequirementLocks.PathFor("cutout");
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps", "--build-constraints", other, "-r", lockPath }));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps", "--build-constraints", "-r", lockPath }));
    }
}
