using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.UITests.Infrastructure;

/// <summary>
/// Writes a deterministic synthetic Gaming library (invented names, generated solid-colour PNG covers, executables that do not
/// exist) into an isolated UI-test data root, using the same storage the app reads. It mirrors
/// <c>tools/generate-synthetic-game-library.ps1</c>: "Synthetic Game 0000".."Synthetic Game NNNN", title key
/// "syntheticgame0000", every 7th a favourite, every 5th played, score = n % 11.
/// </summary>
internal static class SyntheticGameLibrary
{
    internal static string KeyOf(int n) => "syntheticgame" + n.ToString("D4");
    internal static string NameOf(int n) => "Synthetic Game " + n.ToString("D4");

    /// <summary>Creates a new isolated data root (settings included) holding <paramref name="count"/> synthetic games and returns it.</summary>
    internal static string CreateRoot(int count)
    {
        string isolatedRoot = Path.Combine(Repository.Root, ".ai", "artifacts", "ui-data");
        string root = Path.Combine(isolatedRoot, Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["OnboardingCompleted"] = true,
            ["StudioModeEnabled"] = false,
            ["BubbleMode"] = "Disabled"
        }));
        Write(root, count);
        return root;
    }

    internal static void Write(string root, int count)
    {
        string server = Path.Combine(root, "server");
        string covers = Path.Combine(root, "game-covers");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(covers);

        var palette = Enumerable.Range(0, 24).Select(i => (R: (byte)(60 + i * 6), G: (byte)(210 - i * 5), B: (byte)(60 + (i * 37) % 150))).ToArray();
        var storedNames = new string[palette.Length];
        var games = new List<object>();
        var state = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var index = new SortedDictionary<string, string>(StringComparer.Ordinal);
        DateTime epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] statuses = { "", "backlog", "playing", "completed", "onhold", "dropped" };
        for (int i = 0; i < count; i++)
        {
            string key = KeyOf(i);
            games.Add(new
            {
                Id = "synth" + i.ToString("D5"),
                Name = NameOf(i),
                ExecutablePath = @"C:\synthetic-not-installed\game" + i + ".exe",
                Arguments = "", WorkingDirectory = "", Kind = "game", Platform = "", EmulatorId = "", GameFilePath = ""
            });
            bool played = i % 5 == 0;
            state[key] = new
            {
                Favorite = i % 7 == 0, Hidden = false, AddedUtc = epoch.AddMinutes(i),
                Tags = Array.Empty<string>(), Collections = Array.Empty<string>(),
                PlaySeconds = played ? 600 + i : 0,
                LastPlayedUtc = played ? epoch.AddMinutes(i * 3) : (DateTime?)null,
                DefaultInstallationId = (string?)null,
                Status = statuses[i % statuses.Length],
                Score = i % 11
            };
            int slot = i % palette.Length;
            if (storedNames[slot] is null)
            {
                byte[] png = SolidPng(120, 180, palette[slot].R, palette[slot].G, palette[slot].B);
                storedNames[slot] = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()[..32] + ".png";
                File.WriteAllBytes(Path.Combine(covers, storedNames[slot]), png);
            }
            index[key] = storedNames[slot];
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(server, "manual-games.json"), JsonSerializer.Serialize(new { Version = 1, Games = games }, options));
        File.WriteAllText(Path.Combine(server, "game-titles.json"), JsonSerializer.Serialize(new { Version = 1, Titles = state }, options));
        File.WriteAllText(Path.Combine(covers, "covers.json"), JsonSerializer.Serialize(index));
    }

    private static byte[] SolidPng(int width, int height, byte r, byte g, byte b)
    {
        byte[] row = new byte[1 + 3 * width];
        for (int x = 0; x < width; x++) { row[1 + 3 * x] = r; row[2 + 3 * x] = g; row[3 + 3 * x] = b; }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true))
            for (int y = 0; y < height; y++) zs.Write(row, 0, row.Length);
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        byte[] ihdr = BigEndian((uint)width).Concat(BigEndian((uint)height)).Concat(new byte[] { 8, 2, 0, 0, 0 }).ToArray();
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", z.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte[] BigEndian(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(BigEndian((uint)data.Length));
        stream.Write(body);
        stream.Write(BigEndian(Crc32(body)));
    }

    private static uint Crc32(byte[] bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}
