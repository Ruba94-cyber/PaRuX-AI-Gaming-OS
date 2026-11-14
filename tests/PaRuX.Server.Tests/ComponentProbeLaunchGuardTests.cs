using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// 2026-11-13 hang: a user-located "component" that is not a real PE image (the Locate test uses a text stand-in named
/// *.exe) was launched for a version probe. Under the default Windows error mode CreateProcess then shows a modal
/// "not a valid application" dialog from inside the call and never returns, which froze the whole Server suite at 0% CPU.
/// The probe must refuse to launch anything that is not a PE image, and it must do so without waiting on a process.
/// </summary>
public sealed class ComponentProbeLaunchGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-probe-guard-" + Guid.NewGuid().ToString("N"));

    public ComponentProbeLaunchGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_text_stand_in_named_exe_is_not_a_launchable_image()
    {
        string path = Write("stand-in.exe", System.Text.Encoding.UTF8.GetBytes("stand-in for an already-installed component"));
        Assert.False(ComponentInventoryService.IsWindowsPeImage(path));
    }

    [Fact]
    public void Truncated_or_headerless_files_are_not_launchable_images()
    {
        Assert.False(ComponentInventoryService.IsWindowsPeImage(Write("empty.exe", Array.Empty<byte>())));
        Assert.False(ComponentInventoryService.IsWindowsPeImage(Write("mz-only.exe", new byte[] { (byte)'M', (byte)'Z' })));
        byte[] badOffset = new byte[128];
        badOffset[0] = (byte)'M'; badOffset[1] = (byte)'Z';
        badOffset[60] = 0xFF; badOffset[61] = 0xFF; badOffset[62] = 0xFF; badOffset[63] = 0x7F; // e_lfanew far past the end
        Assert.False(ComponentInventoryService.IsWindowsPeImage(Write("bad-offset.exe", badOffset)));
        byte[] noSignature = new byte[128];
        noSignature[0] = (byte)'M'; noSignature[1] = (byte)'Z'; noSignature[60] = 64; // points at zeros, not "PE\0\0"
        Assert.False(ComponentInventoryService.IsWindowsPeImage(Write("no-signature.exe", noSignature)));
        Assert.False(ComponentInventoryService.IsWindowsPeImage(Path.Combine(_dir, "missing.exe")));
    }

    [Fact]
    public void A_real_pe_image_is_accepted()
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        Assert.True(ComponentInventoryService.IsWindowsPeImage(host));
        byte[] minimal = new byte[128];
        minimal[0] = (byte)'M'; minimal[1] = (byte)'Z'; minimal[60] = 64;
        minimal[64] = (byte)'P'; minimal[65] = (byte)'E';
        Assert.True(ComponentInventoryService.IsWindowsPeImage(Write("minimal-pe.exe", minimal)));
    }

    [Fact]
    public async Task The_version_probe_skips_a_non_pe_exe_without_launching_it()
    {
        Assert.True(OperatingSystem.IsWindows(), "The launch guard is a Windows behaviour.");
        string path = Write("stand-in-probe.exe", System.Text.Encoding.UTF8.GetBytes("stand-in for an already-installed component"));
        // Bounded: a regression must fail this test by timeout instead of hanging the whole suite inside CreateProcess.
        string? version = await ComponentInventoryService.ProbeVersionAsync("git", path, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(version);
    }
}
