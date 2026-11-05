using System;
using System.IO;
using System.Linq;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Downloads;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>No peers or torrent network activity; DPAPI fixture runs on Windows.</summary>
public sealed class NativeTorrentRecoveryStoreTests
{
    [Fact]
    public void Windows_resume_manifest_is_encrypted_and_requires_explicit_owner_resume()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Directory.CreateTempSubdirectory("parux-torrent-dpapi-").FullName;
        string path = Path.Combine(dir, "recovery.dpapi");
        try
        {
            string privateMagnet = "magnet:?xt=urn:btih:" + new string('b', 40)
                + "&tr=https%3A%2F%2Ftracker.example%2Fsecret-passkey-1234";
            var entry = new NativeTorrentRecoveryStore.Entry(
                Guid.NewGuid().ToString("N"), GameDownloadKinds.Magnet, privateMagnet,
                "magnet/" + new string('b', 40), "test.zip", dir, new string('b', 40),
                null, 0, DateTime.UtcNow, 345, 12345);
            var store = new NativeTorrentRecoveryStore(path);
            store.Save(new[] { entry });
            string raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("magnet:?", raw);
            Assert.DoesNotContain("secret-passkey-1234", raw);
            var restored = Assert.Single(store.Load());
            Assert.Equal(entry.Target, restored.Target);
            Assert.Equal(entry.Id, restored.Id);
            Assert.Equal(345, restored.BytesDone);
            store.Save(Array.Empty<NativeTorrentRecoveryStore.Entry>());
            Assert.False(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Native_job_restores_paused_without_network_and_redacts_private_tracker()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Directory.CreateTempSubdirectory("parux-torrent-resume-").FullName;
        try
        {
            string history = Path.Combine(dir, "history.json");
            string hash = new string('a', 40);
            string magnet = "magnet:?xt=urn:btih:" + hash +
                "&tr=https%3A%2F%2Ftracker.example%2Fmy-secret-passkey";
            string id = Guid.NewGuid().ToString("N");
            new NativeTorrentRecoveryStore(history + ".torrent.dpapi").Save(new[] {
                new NativeTorrentRecoveryStore.Entry(id, GameDownloadKinds.Magnet,
                    magnet, "magnet/" + hash, "fixture.bin", dir, hash,
                    null, 3, DateTime.UtcNow, 128, 1024)
            });
            var native = new EmbeddedTorrentTransport(() => false,
                Path.Combine(dir, "native-cache"));
            // Pure local queue reconstruction. A network worker only starts
            // when an explicit Resume is sent; this test never sends one.
            using var manager = new GameDownloadManager(
                new System.Net.Http.HttpClient(), () => false,
                _ => long.MaxValue, history, Array.Empty<TimeSpan>(),
                torrent: null, nativeTorrent: native);
            GameDownloadDto restored = Assert.Single(manager.List());
            Assert.Equal(id, restored.Id);
            Assert.Equal(GameDownloadStates.Paused, restored.State);
            Assert.Contains("Resume", restored.Reason);
            Assert.Equal("magnet/" + hash, restored.Source);
            Assert.DoesNotContain("my-secret-passkey", restored.Source);
            Assert.False(Directory.Exists(
                Path.Combine(dir, ".parux-native-torrent-" + id)));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Tampered_encrypted_state_cannot_be_loaded_as_a_source()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Directory.CreateTempSubdirectory("parux-torrent-corrupt-").FullName;
        string path = Path.Combine(dir, "recovery.dpapi");
        try
        {
            var store = new NativeTorrentRecoveryStore(path);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("{\"url\":\"magnet:unsafe\"}"));
            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
