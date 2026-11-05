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
