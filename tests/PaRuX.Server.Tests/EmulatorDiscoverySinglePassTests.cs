using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Emulators;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Synthetic local executables only; no installed emulators, firmware or game files.</summary>
public sealed class EmulatorDiscoverySinglePassTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-detect-" + Guid.NewGuid().ToString("N"));

    public EmulatorDiscoverySinglePassTests() => Directory.CreateDirectory(_root);

    private string FakeExe(string relative)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[20_000]);
        return path;
    }

    [Fact]
    public void One_directory_visit_finds_multiple_emulators_with_a_tight_global_budget()
    {
        FakeExe("Portable/Cemu.exe");
        FakeExe("Portable/Dolphin.exe");
        var detected = EmulatorDiscovery.Detect(new[] { _root }, maxEntries: 2);
        Assert.Contains(detected, d => d.EmulatorId == "cemu");
        Assert.Contains(detected, d => d.EmulatorId == "dolphin");
        Assert.Equal(2, detected.Count);
    }

    [Fact]
    public void Root_fairness_prevents_an_earlier_root_from_hiding_another_emulator()
    {
        string first = Path.Combine(_root, "A");
        string second = Path.Combine(_root, "B");
        Directory.CreateDirectory(first);
        FakeExe("A/Child/Unused.exe");
        string emulator = FakeExe("B/PPSSPPWindows64.exe");
        var detected = EmulatorDiscovery.Detect(new[] { first, first, second }, maxEntries: 3);
        var entry = Assert.Single(detected);
        Assert.Equal("ppsspp", entry.EmulatorId);
        Assert.Equal(emulator, entry.ExecutablePath);
    }

    [Fact]
    public void Detection_does_not_follow_linked_emulator_folders()
    {
        FakeExe("real/Cemu.exe");
        string link = Path.Combine(_root, "alias");
        try { Directory.CreateSymbolicLink(link, Path.Combine(_root, "real")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Link creation can be disabled by local OS policy.
        }
        Assert.Empty(EmulatorDiscovery.Detect(new[] { link }));
    }

    [Fact]
    public void Detect_honors_a_canceled_scan_before_inspecting_emulator_files()
    {
        FakeExe("Portable/Cemu.exe");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            EmulatorDiscovery.Detect(new[] { _root }, stop.Token));
    }

    [Fact]
    public void Import_fails_closed_when_Gaming_is_already_active()
    {
        string exe = FakeExe("Eden/eden.exe");
        string rom = FakeExe("Games/First.nro");
        var library = new ManualGameLibrary(Path.Combine(_root, "blocked-library.json"));
        var outcome = EmulatorEndpoints.Import(
            new EmulatorImportRequest("eden", exe, new() { rom }, true, null, null),
            library, () => true);
        Assert.Equal(0, outcome.Added);
        Assert.Empty(library.List());
        Assert.Contains(outcome.Problems, p => p.Contains("Gaming", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_preserves_a_truthful_partial_result_when_Gaming_starts_mid_batch()
    {
        string exe = FakeExe("Eden/eden.exe");
        string first = FakeExe("Games/First.nro");
        string second = FakeExe("Games/Second.nro");
        var library = new ManualGameLibrary(Path.Combine(_root, "partial-library.json"));
        bool GamingBegan() => library.List().Count > 0;

        var outcome = EmulatorEndpoints.Import(
            new EmulatorImportRequest("eden", exe, new() { first, second }, true, null, null),
            library, GamingBegan);

        Assert.Equal(1, outcome.Added);
        Assert.Equal(0, outcome.Skipped);
        Assert.Equal("First", Assert.Single(library.List()).Name);
        Assert.Contains(outcome.Problems, p => p.Contains("Gaming started", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
