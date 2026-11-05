using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Downloads;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Synthetic v1 torrent metadata and inert local payloads. Does not contact peers,
/// exercise real network integrity, or represent a successful Windows acceptance.
/// </summary>
public sealed class EmbeddedMultifileTorrentTests
{
    private static byte[] MultiTorrentBytes()
    {
        byte[] header = Encoding.Latin1.GetBytes(
            "d8:announce20:http://t.example/ann4:infod5:filesl"
            + "d6:lengthi4e4:pathl3:sub5:a.txtee"
            + "d6:lengthi2e4:pathl3:sub5:b.txteee"
            + "4:name4:demo12:piece lengthi16384e6:pieces20:");
        return header.Concat(new byte[20]).Concat(Encoding.Latin1.GetBytes("ee")).ToArray();
    }

    private static GameDownloadJob Job(string folder, string name, bool autoInstall = false)
    {
        byte[] bytes = MultiTorrentBytes();
        return new GameDownloadJob {
            Id = Guid.NewGuid().ToString("N"), Kind = GameDownloadKinds.TorrentFile,
            InfoHash = GameDownloadSourceValidator.ParseTorrent(bytes).InfoHash,
            Target = "fixture", Source = "torrent-file/synthetic",
            Directory = folder, FileName = name, AutoInstallPortable = autoInstall
        };
    }

    [Fact]
    public void Native_multifile_requires_explicit_zip_and_refuses_implicit_auto_install()
    {
        byte[] bytes = MultiTorrentBytes();
        var valid = Job(Path.GetTempPath(), "payload.zip");
        Torrent metadata = EmbeddedTorrentTransport.DecodeAndAdmit(valid, bytes, 100);
        Assert.Equal(2, metadata.Files.Count);
        Assert.Equal(6, metadata.Size);
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.DecodeAndAdmit(Job(Path.GetTempPath(), "payload.exe"), bytes, 100));
        Assert.Throws<GameDownloadException>(() =>
            EmbeddedTorrentTransport.DecodeAndAdmit(Job(Path.GetTempPath(), "payload.zip", true), bytes, 100));
    }

    [Fact]
    public async Task Native_multifile_bundle_stages_zip_without_auto_install_or_publication()
    {
        string folder = Directory.CreateTempSubdirectory("parux-multi-").FullName;
        try
        {
            var job = Job(folder, "payload.zip");
            Torrent metadata = EmbeddedTorrentTransport.DecodeAndAdmit(job, MultiTorrentBytes(), 100);
            string stage = EmbeddedTorrentTransport.StagingPath(job);
            foreach (var item in metadata.Files)
            {
                string path = Path.Combine(stage, item.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, item.Path.EndsWith("a.txt", StringComparison.Ordinal)
                    ? new byte[] { 1, 2, 3, 4 } : new byte[] { 5, 6 });
            }
            var context = new GameDownloadTransferContext {
                Job = job, GamingActive = () => false, FreeBytes = _ => long.MaxValue,
                MaxFileBytes = 100, Throttle = (_, _) => Task.CompletedTask,
                Report = (_, _) => { }, UserCancelled = () => false
            };
            await EmbeddedTorrentTransport.BundleVerifiedFilesAsync(
                context, stage, metadata, CancellationToken.None);
            using var zip = System.IO.Compression.ZipFile.OpenRead(job.PartPath);
            Assert.Equal(2, zip.Entries.Count);
            Assert.Equal(6, zip.Entries.Sum(x => x.Length));
            Assert.False(File.Exists(job.FinalPath));
            Assert.Empty(Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly));
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }
}
