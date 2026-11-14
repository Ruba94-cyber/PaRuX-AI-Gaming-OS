using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-app finding (Avatar, insightface==0.7.3 is sdist-only): on a PC with Visual Studio 2022 the managed install failed
/// with "Unable to find a compatible Visual Studio installation" because vswhere.exe was not on PATH, and the user saw only
/// the first 1200 characters of the uv log (the real error is printed last). The failure is now typed, the log keeps its
/// tail, and the Visual Studio Installer folder (vswhere.exe) is added to the child PATH only.
/// </summary>
public sealed class ManagedBuildToolsRequiredTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-vs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string RealLogTail =
        "  × Failed to build `insightface==0.7.3`\n  ├─▶ The build backend returned an error\n  ╰─▶ Call to `setuptools.build_meta.build_wheel` failed (exit code: 1)\n"
        + "      running build_ext\n      building 'insightface.thirdparty.face3d.mesh.cython.mesh_core_cython' extension\n\n"
        + "      error: Unable to find a compatible Visual Studio installation.\n\n\nhint: Build failures usually indicate a problem with the package or the build environment\n";

    [Fact]
    public void A_source_build_that_cannot_find_the_toolchain_is_recognised_with_its_package()
    {
        Assert.True(ManagedBuildToolsFailure.IsMissingToolchain(RealLogTail, out string package));
        Assert.Equal("insightface", package);
    }

    [Fact]
    public void A_message_that_uv_hard_wrapped_across_lines_is_still_recognised()
    {
        string wrapped = "  × Failed to build `insightface==0.7.3`\n      error: Microsoft Visual C++ 14.0 or greater is\n      required. Get it with \"Microsoft C++ Build Tools\":\n";
        Assert.True(ManagedBuildToolsFailure.IsMissingToolchain(wrapped, out string package));
        Assert.Equal("insightface", package);
    }

    [Theory]
    [InlineData("error: Microsoft Visual C++ 14.0 or greater is required. Get it with \"Microsoft C++ Build Tools\"")]
    [InlineData("error: Unable to find vcvarsall.bat")]
    public void The_other_known_missing_compiler_messages_are_recognised(string log)
    {
        Assert.True(ManagedBuildToolsFailure.IsMissingToolchain("Failed to build `decord==0.6.0`\n" + log, out string package));
        Assert.Equal("decord", package);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  × Failed to download `numpy==2.4.6`\n  ╰─▶ Hash mismatch for `numpy==2.4.6`")]
    [InlineData("error: Failed to fetch: https://pypi.org/simple/numpy/\n  Caused by: dns error")]
    [InlineData("LINK : fatal error LNK1104: cannot open file 'x.pyd'")]
    public void Unrelated_failures_are_not_reported_as_missing_build_tools(string log)
    {
        Assert.False(ManagedBuildToolsFailure.IsMissingToolchain(log, out _));
    }

    [Fact]
    public void The_typed_failure_names_the_package_and_the_remediation_without_claiming_success()
    {
        var failure = new ManagedBuildToolsRequiredException("insightface", "compiler output");

        Assert.Equal("insightface", failure.Package);
        Assert.Contains("C++ build tools are required", failure.Message);
        Assert.Contains("Desktop development with C++", failure.Message);
        Assert.Contains("does not install it for you", failure.Message);
        Assert.Contains("compiler output", failure.Message);
        Assert.Equal("build_tools_required", ManagedBuildToolsFailure.Code);
    }

    [Fact]
    public void A_long_tool_log_keeps_its_start_and_its_end_where_the_real_error_is()
    {
        string log = "START-" + new string('a', 5000) + "-THE-REAL-ERROR";

        string trimmed = ManagedPythonRuntime.Trim(log);

        Assert.StartsWith("START-", trimmed);
        Assert.EndsWith("-THE-REAL-ERROR", trimmed);
        Assert.True(trimmed.Length < 2000);
        Assert.Equal("short log", ManagedPythonRuntime.Trim("  short log \n"));
    }

    private string FakeProgramFiles(bool withVswhere)
    {
        string root = Path.Combine(_dir, "pf86");
        string installer = Path.Combine(root, "Microsoft Visual Studio", "Installer");
        Directory.CreateDirectory(installer);
        if (withVswhere) File.WriteAllText(Path.Combine(installer, "vswhere.exe"), "x");
        return root;
    }

    [Fact]
    public void The_installer_folder_is_appended_to_the_child_path_once_and_only_when_vswhere_exists()
    {
        string root = FakeProgramFiles(withVswhere: true);
        string installer = Path.Combine(root, "Microsoft Visual Studio", "Installer");
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"C:\Windows;C:\Tools" };

        ManagedPythonRuntime.AppendVisualStudioInstallerToPath(env, root);
        ManagedPythonRuntime.AppendVisualStudioInstallerToPath(env, root);

        Assert.Equal(@"C:\Windows;C:\Tools;" + installer, env["Path"]);
    }

    [Fact]
    public void Nothing_is_added_when_vswhere_is_absent_or_already_on_the_path()
    {
        string missing = FakeProgramFiles(withVswhere: false);
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"C:\Windows" };
        ManagedPythonRuntime.AppendVisualStudioInstallerToPath(env, missing);
        Assert.Equal(@"C:\Windows", env["Path"]);

        string present = Path.Combine(_dir, "pf86b");
        string installer = Path.Combine(present, "Microsoft Visual Studio", "Installer");
        Directory.CreateDirectory(installer);
        File.WriteAllText(Path.Combine(installer, "vswhere.exe"), "x");
        env["Path"] = @"C:\Windows;" + installer + @"\";
        ManagedPythonRuntime.AppendVisualStudioInstallerToPath(env, present);
        Assert.Equal(@"C:\Windows;" + installer + @"\", env["Path"]);
    }
}
