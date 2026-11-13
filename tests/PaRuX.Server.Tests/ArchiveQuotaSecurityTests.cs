using System.Diagnostics;
using System.IO.Compression;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F13: extraction quotas, link safety and layout rules for managed archives.</summary>
public sealed class ArchiveQuotaSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-archive-quota-" + Guid.NewGuid().ToString("N"));

    public ArchiveQuotaSecurityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string WriteZip(string name, params (string Name, byte[] Content)[] entries)
    {
        string path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entryName, byte[] content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.SmallestSize);
            using Stream stream = entry.Open();
            stream.Write(content);
        }
        return path;
    }

    private static byte[] Text(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    [Fact]
    public async Task Too_many_entries_are_refused_before_anything_is_written()
    {
        string archive = WriteZip("many.zip", Enumerable.Range(0, 25).Select(i => ("f" + i + ".txt", Text("x"))).ToArray());
        string destination = Path.Combine(_root, "many-out");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None, maxEntries: 10));
        Assert.False(Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any());
    }

    [Fact]
    public async Task A_zip_bomb_is_stopped_by_the_compression_ratio_before_the_disk_fills()
    {
        // 80 MiB of zeros deflates to roughly 80 KiB (ratio of about 1000), far above the 200:1 limit.
        string archive = WriteZip("bomb.zip", ("zeros.bin", new byte[80 * 1024 * 1024]));
        string destination = Path.Combine(_root, "bomb-out");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None));
        Assert.Contains("decompression bomb", error.Message);
        Assert.False(File.Exists(Path.Combine(destination, "zeros.bin")));
        Assert.True(!Directory.Exists(destination) || new DirectoryInfo(destination).EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length) <= SafeArchiveExtractor.RatioGraceBytes + 2 * 1024 * 1024);
    }

    [Fact]
    public async Task A_normal_compressible_archive_below_the_ratio_still_extracts()
    {
        string archive = WriteZip("normal.zip", ("readme.txt", Text(string.Concat(Enumerable.Repeat("hello world ", 20_000)))));
        string destination = Path.Combine(_root, "normal-out");

        await SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None);
        Assert.Equal(240_000, new FileInfo(Path.Combine(destination, "readme.txt")).Length);
    }

    [Fact]
    public async Task Zip_slip_entry_is_rejected_and_nothing_escapes()
    {
        string archive = WriteZip("slip.zip", ("../../outside.txt", Text("escaped")));
        string destination = Path.Combine(_root, "slip-out");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task An_existing_junction_in_the_destination_is_never_followed()
    {
        string outside = Path.Combine(_root, "outside");
        string destination = Path.Combine(_root, "junction-out");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(destination);
        string junction = Path.Combine(destination, "bin");
        if (!TryCreateJunction(junction, outside)) return; // cannot create a junction on this volume
        string archive = WriteZip("junction.zip", ("bin/payload.dll", Text("planted")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(outside, "payload.dll")));
    }

    [Fact]
    public async Task Source_archive_extracts_the_single_top_level_folder_into_the_destination()
    {
        string archive = WriteZip("src.zip", ("repo-abc/LICENSE", Text("MIT")), ("repo-abc/pkg/mod.py", Text("print(1)")));
        string destination = Path.Combine(_root, "managed-src");

        await ManagedSourceArchive.ExtractArchiveAsync(archive, destination, CancellationToken.None);
        Assert.Equal("MIT", File.ReadAllText(Path.Combine(destination, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(destination, "pkg", "mod.py")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "managed-src.staging-*"));
    }

    [Fact]
    public async Task Source_archive_zip_slip_leaves_no_partial_tree()
    {
        string archive = WriteZip("src-slip.zip", ("repo-abc/ok.txt", Text("ok")), ("repo-abc/../../escaped.txt", Text("bad")));
        string destination = Path.Combine(_root, "managed-slip");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedSourceArchive.ExtractArchiveAsync(archive, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "managed-slip.staging-*"));
    }

    [Fact]
    public async Task Source_archive_symlink_entry_is_refused()
    {
        string archive = Path.Combine(_root, "src-link.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            ZipArchiveEntry link = zip.CreateEntry("repo-abc/link");
            link.ExternalAttributes = unchecked((int)0xA0000000);
            using var writer = new StreamWriter(link.Open());
            writer.Write("C:/Windows/System32");
        }
        string destination = Path.Combine(_root, "managed-link");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedSourceArchive.ExtractArchiveAsync(archive, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Source_archive_with_an_oversized_entry_count_is_refused()
    {
        string archive = WriteZip("src-many.zip", Enumerable.Range(0, 30).Select(i => ("repo-abc/f" + i, Text("x"))).ToArray());
        string destination = Path.Combine(_root, "managed-many");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedSourceArchive.ExtractArchiveAsync(archive, destination, CancellationToken.None, maxEntries: 10));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Source_archive_with_two_top_level_entries_is_refused()
    {
        string archive = WriteZip("src-two.zip", ("repo-abc/a.txt", Text("a")), ("stray.txt", Text("b")));
        string destination = Path.Combine(_root, "managed-two");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedSourceArchive.ExtractArchiveAsync(archive, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Source_archive_zip_bomb_is_refused_by_the_ratio()
    {
        string archive = WriteZip("src-bomb.zip", ("repo-abc/zeros.bin", new byte[80 * 1024 * 1024]));
        string destination = Path.Combine(_root, "managed-bomb");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedSourceArchive.ExtractArchiveAsync(archive, destination, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var info = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using Process process = Process.Start(info)!;
            process.WaitForExit(15000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception) { return false; }
    }
}
