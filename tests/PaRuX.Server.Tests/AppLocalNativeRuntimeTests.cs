using System.Diagnostics;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The portable editions must not need an installed Visual C++ redistributable: the runtime
/// ships beside the Core and native engines started from other folders find it through PATH.
/// </summary>
public sealed class AppLocalNativeRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-vcrt-" + Guid.NewGuid().ToString("N"));

    public AppLocalNativeRuntimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Core_folder_with_the_runtime_is_put_first_on_the_engine_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_dir, "vcruntime140.dll"), "x");
        var psi = new ProcessStartInfo("llama-server.exe");
        psi.Environment["PATH"] = @"C:\Windows\System32;C:\Tools";

        LocalAiProcessManager.ApplyAppLocalNativeRuntime(psi, _dir + Path.DirectorySeparatorChar);

        Assert.Equal(_dir + @";C:\Windows\System32;C:\Tools", psi.Environment["PATH"]);
    }

    [Fact]
    public void Applying_twice_does_not_duplicate_the_entry()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_dir, "vcruntime140.dll"), "x");
        var psi = new ProcessStartInfo("llama-server.exe");
        psi.Environment["PATH"] = @"C:\Windows\System32";

        LocalAiProcessManager.ApplyAppLocalNativeRuntime(psi, _dir);
        LocalAiProcessManager.ApplyAppLocalNativeRuntime(psi, _dir.ToUpperInvariant() + "\\");

        Assert.Equal(_dir + @";C:\Windows\System32", psi.Environment["PATH"]);
    }

    [Fact]
    public void Installed_editions_without_an_app_local_runtime_keep_their_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = new ProcessStartInfo("llama-server.exe");
        psi.Environment["PATH"] = @"C:\Windows\System32";

        LocalAiProcessManager.ApplyAppLocalNativeRuntime(psi, _dir);

        Assert.Equal(@"C:\Windows\System32", psi.Environment["PATH"]);
    }

    [Fact]
    public void Both_portable_builders_bundle_the_signed_runtime_and_require_it_in_the_zip()
    {
        string assets = Read("scripts", "portable-offline-assets.ps1");
        string standard = Read("scripts", "build-portable.ps1");
        string corporate = Read("scripts", "build-corporate-lite.ps1");
        Assert.Contains("function Copy-PaRuXVcRuntime", assets, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", assets, StringComparison.Ordinal);
        Assert.Contains("$signature.Status -ne \"Valid\"", assets, StringComparison.Ordinal);
        Assert.Contains("Copy-PaRuXVcRuntime -Destinations", standard, StringComparison.Ordinal);
        Assert.Contains("$required += \"PaRuX.Server/\" + $vcFile", standard, StringComparison.Ordinal);
        Assert.Contains("Copy-PaRuXVcRuntime -Destinations", corporate, StringComparison.Ordinal);
        Assert.Contains("Visual C++ runtime not staged", corporate, StringComparison.Ordinal);
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
