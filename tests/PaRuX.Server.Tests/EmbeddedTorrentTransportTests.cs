using System;
using System.IO;
using System.Linq;
using System.Text;
using MonoTorrent;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Downloads;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Offline synthetic metadata tests only. No tracker, peer, public torrent, network,
/// downloaded commercial title or external torrent client is needed.
/// A real peer-transfer/Windows end-to-end acceptance is still required.
/// </summary>
public sealed class EmbeddedTorrentTransportTests
{
    private static byte[] TorrentBytes(string name, long length)
    {
        // One metadata block, one 16 KiB piece, dummy SHA-1 checksum. This tests
        // metadata admission, never completion of a real peer transfer.
        byte[] prefix = Encoding.Latin1.GetBytes(
            $"d8:announce20:http://t.example/ann4:infod6:lengthi{length}e4:name{name.Length}:{name}12:piece lengthi16384e6:pieces20:");
        return prefix.Concat(new byte[20]).Concat(Encoding.Latin1.GetBytes("ee")).ToArray();
    }

    private static GameDownloadJob Job(byte[] bytes, string folder, string kind = GameDownloadKinds.TorrentFile)
    {
        string hash = GameDownloadSourceValidator.ParseTorrent(bytes).InfoHash;
        return new GameDownloadJob
        {
            Id = "ab12cd34ef56", Kind = kind, InfoHash = hash, Target = "synthetic",
            Directory = folder, FileName = "download.bin", Source = "torrent-file/synthetic"
        };
    }

    [Fact]
    public void Bundled_engine_admits_bounded_single_file_metadata_without_network()
    {
        string directory = Path.GetTempPath();
        byte[] bytes = TorrentBytes("open-demo.bin", 1234);
        Torrent metadata = EmbeddedTorrentTransport.DecodeAndAdmit(Job(bytes, directory), bytes, 10_000);
        Assert.Equal(1, metadata.Files.Count);
        Assert.Equal("open-demo.bin", metadata.Files[0].Path);
        Assert.Equal(1234, metadata.Size);
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("unsafe/name")]
    [InlineData("x\\evil.bin")]
    [InlineData("CON.bin")]
    [InlineData("end.")]
    public void Embedded_engine_refuses_unsafe_names_before_starting_a_torrent(string name)
    {
        byte[] bytes = TorrentBytes(name, 1000);
        GameDownloadJob job = Job(bytes, Path.GetTempPath());
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.DecodeAndAdmit(job, bytes, 10_000));
    }

    [Fact]
    public void Embedded_engine_refuses_changed_content_identity_and_oversize()
    {
        byte[] bytes = TorrentBytes("safe.bin", 1000);
        GameDownloadJob job = Job(bytes, Path.GetTempPath());
        var forged = new GameDownloadJob
        {
            Id = job.Id, Kind = job.Kind, Directory = job.Directory, FileName = job.FileName,
            Source = job.Source, Target = job.Target, InfoHash = new string('0', 40)
        };
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.DecodeAndAdmit(forged, bytes, 10_000));
    }

    [Fact]
    public void Embedded_engine_metadata_size_is_limited_before_peer_transfer()
    {
        byte[] bytes = TorrentBytes("open-data.bin", 5000);
        GameDownloadJob job = Job(bytes, Path.GetTempPath());
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.DecodeAndAdmit(job, bytes, 4999));
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.DecodeAndAdmit(job, new byte[(int)GameDownloadSourceValidator.MaxTorrentFileBytes + 1], 50_000));
    }

    [Fact]
    public void Torrent_copy_accounting_refuses_truncated_or_extended_stage_payloads()
    {
        // Synthetic source-only accounting: no tracker, peer or executable.
        Assert.Equal(4L, EmbeddedTorrentTransport.AdvanceVerifiedPayload(0, 4, 6));
        Assert.Equal(6L, EmbeddedTorrentTransport.AdvanceVerifiedPayload(4, 2, 6));
        EmbeddedTorrentTransport.RequireExpectedPayloadLength(6, 6);
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.RequireExpectedPayloadLength(4, 6));
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.AdvanceVerifiedPayload(4, 3, 6));
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.AdvanceVerifiedPayload(0, -1, 6));
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.AdvanceVerifiedPayload(long.MaxValue, 1, long.MaxValue));
    }

    [Fact]
    public async System.Threading.Tasks.Task Single_file_native_copy_uses_private_stage_without_publishing_or_running_anything()
    {
        string folder = Directory.CreateTempSubdirectory("parux-single-native-").FullName;
        try
        {
            byte[] torrentBytes = TorrentBytes("safe.bin", 4);
            var job = new GameDownloadJob
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = GameDownloadKinds.TorrentFile,
                Target = "synthetic-local-metadata",
                FileName = "manual-output.bin",
                Directory = folder,
                Source = "torrent-file/synthetic",
                InfoHash = GameDownloadSourceValidator.ParseTorrent(torrentBytes).InfoHash
            };
            Torrent metadata = EmbeddedTorrentTransport.DecodeAndAdmit(job, torrentBytes, 16);
            string stage = EmbeddedTorrentTransport.StagingPath(job);
            Directory.CreateDirectory(stage);
            byte[] inert = { 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(stage, "safe.bin"), inert);
            long reported = -1;
            var context = new GameDownloadTransferContext
            {
                Job = job, GamingActive = () => false,
                FreeBytes = _ => long.MaxValue, MaxFileBytes = 16,
                Throttle = (_, _) => System.Threading.Tasks.Task.CompletedTask,
                Report = (current, _) => reported = current,
                UserCancelled = () => false
            };
            await EmbeddedTorrentTransport.CopyVerifiedFileAsync(
                context, stage, metadata, System.Threading.CancellationToken.None);
            Assert.Equal(inert, File.ReadAllBytes(job.PartPath));
            Assert.Equal(4L, reported);
            Assert.False(File.Exists(job.FinalPath)); // publication belongs to the download manager
            Assert.True(File.Exists(Path.Combine(stage, "safe.bin")));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Gaming_started_during_native_single_file_throttle_blocks_the_write()
    {
        string folder = Directory.CreateTempSubdirectory("parux-native-hardoff-").FullName;
        try
        {
            byte[] torrentBytes = TorrentBytes("safe.bin", 4);
            var job = new GameDownloadJob
            {
                Id = Guid.NewGuid().ToString("N"), Kind = GameDownloadKinds.TorrentFile,
                FileName = "download.bin", Directory = folder,
                Source = "torrent-file/synthetic",
                InfoHash = GameDownloadSourceValidator.ParseTorrent(torrentBytes).InfoHash
            };
            Torrent metadata = EmbeddedTorrentTransport.DecodeAndAdmit(job, torrentBytes, 16);
            string stage = EmbeddedTorrentTransport.StagingPath(job);
            Directory.CreateDirectory(stage);
            File.WriteAllBytes(Path.Combine(stage, "safe.bin"), new byte[] { 1, 2, 3, 4 });
            bool gaming = false;
            var context = new GameDownloadTransferContext
            {
                Job = job, GamingActive = () => gaming,
                FreeBytes = _ => long.MaxValue, MaxFileBytes = 16,
                Throttle = (_, _) => { gaming = true; return System.Threading.Tasks.Task.CompletedTask; },
                Report = (_, _) => { }, UserCancelled = () => false
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                EmbeddedTorrentTransport.CopyVerifiedFileAsync(
                    context, stage, metadata, System.Threading.CancellationToken.None));
            Assert.False(File.Exists(job.FinalPath));
            Assert.Equal(0L, new FileInfo(job.PartPath).Length); // no payload write after Gaming hard-off
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Cancelled_owned_nested_torrent_stage_is_removed_without_touching_sibling_user_files()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-stage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var job = new GameDownloadJob
            {
                Id = Guid.NewGuid().ToString("N"), FileName = "fixture.zip",
                Directory = folder, Kind = GameDownloadKinds.TorrentFile,
                Source = "torrent-file/synthetic"
            };
            string stage = EmbeddedTorrentTransport.StagingPath(job);
            Directory.CreateDirectory(Path.Combine(stage, "data", "levels"));
            File.WriteAllText(Path.Combine(stage, "data", "levels", "level.txt"), "test fixture");
            string other = Path.Combine(folder, "my-own-save.txt");
            File.WriteAllText(other, "preserve");
            EmbeddedTorrentTransport.CleanOwnedStaging(job);
            Assert.False(Directory.Exists(stage));
            Assert.Equal("preserve", File.ReadAllText(other));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Unsafe_link_in_owned_torrent_stage_prevents_recursive_cleanup()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-link-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? link = null;
        try
        {
            var job = new GameDownloadJob
            {
                Id = Guid.NewGuid().ToString("N"), FileName = "fixture.zip",
                Directory = folder, Kind = GameDownloadKinds.TorrentFile,
                Source = "torrent-file/synthetic"
            };
            string stage = EmbeddedTorrentTransport.StagingPath(job);
            Directory.CreateDirectory(stage);
            string outside = Path.Combine(folder, "outside.txt");
            File.WriteAllText(outside, "unchanged");
            link = Path.Combine(stage, "suspicious-link.txt");
            try { File.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { return; } // Windows developer mode may forbid symlinks.
            EmbeddedTorrentTransport.CleanOwnedStaging(job);
            Assert.True(Directory.Exists(stage));
            Assert.Equal("unchanged", File.ReadAllText(outside));
        }
        finally
        {
            if (link is not null)
                try { File.Delete(link); } catch (Exception) { }
            try { Directory.Delete(folder, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public void Resume_refuses_a_stage_containing_a_link_before_any_peer_activity()
    {
        string folder = Directory.CreateTempSubdirectory("parux-stage-preflight-").FullName;
        try
        {
            var job = new GameDownloadJob
            {
                Id = Guid.NewGuid().ToString("N"), FileName = "fixture.zip",
                Directory = folder, Kind = GameDownloadKinds.TorrentFile,
                Source = "torrent-file/synthetic"
            };
            string stage = EmbeddedTorrentTransport.StagingPath(job);
            Directory.CreateDirectory(stage);
            string outside = Path.Combine(folder, "outside.txt");
            File.WriteAllText(outside, "safe");
            string link = Path.Combine(stage, "link.txt");
            try { File.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { return; }
            Assert.Throws<GameDownloadException>(() => EmbeddedTorrentTransport.EnsureOwnedStaging(job, stage));
            Assert.Equal("safe", File.ReadAllText(outside));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public void Resume_accepts_existing_regular_nested_stage_without_deleting_parts()
    {
        string folder = Directory.CreateTempSubdirectory("parux-stage-resume-").FullName;
        try
        {
            var job = new GameDownloadJob
            {
                Id = Guid.NewGuid().ToString("N"), FileName = "fixture.zip",
                Directory = folder, Kind = GameDownloadKinds.TorrentFile,
                Source = "torrent-file/synthetic"
            };
            string stage = EmbeddedTorrentTransport.StagingPath(job);
            string nested = Path.Combine(stage, "folder");
            Directory.CreateDirectory(nested);
            string part = Path.Combine(nested, "part.bin");
            File.WriteAllText(part, "existing data");
            EmbeddedTorrentTransport.EnsureOwnedStaging(job, stage);
            Assert.Equal("existing data", File.ReadAllText(part));
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (Exception) { } }
    }

    [Fact]
    public void The_integrated_transport_is_opt_in_per_job_and_never_starts_during_gaming()
    {
        string folder = Path.Combine(Path.GetTempPath(), "parux-native-torrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var native = new EmbeddedTorrentTransport(() => true, Path.Combine(folder, "cache"));
            using var manager = new GameDownloadManager(new System.Net.Http.HttpClient(), () => true,
                _ => long.MaxValue, null, Array.Empty<TimeSpan>(), null, native);
            string magnet = "magnet:?xt=urn:btih:" + new string('1', 40);
            Assert.Throws<GameDownloadException>(() => manager.Enqueue(
                new GameDownloadRequest(magnet, folder, Kind: GameDownloadKinds.Magnet)));
            Assert.Empty(manager.List());
            Assert.Empty(Directory.EnumerateDirectories(folder, ".parux-native-torrent-*"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
