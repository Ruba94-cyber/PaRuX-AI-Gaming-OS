using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Server.Orchestration.Gaming.Downloads;

/// <param name="Normalized">The link to hand to the client (kept in memory only; may contain trackers).</param>
/// <param name="InfoHashHex">Lowercase 40-hex info-hash; the only part of a magnet that is ever shown or stored.</param>
internal sealed record GameMagnetLink(string Normalized, string InfoHashHex, string? DisplayName);

/// <param name="InfoHashHex">SHA-1 of the bencoded info dictionary, lowercase hex.</param>
internal sealed record GameTorrentFile(string FullPath, string Name, string InfoHashHex, long ContentLength);

/// <summary>Strict validation of torrent sources. Rejects anything not clearly well-formed; never fetches anything.</summary>
internal static class GameDownloadSourceValidator
{
    public const int MaxMagnetLength = 2048;
    public const int MaxMagnetParameters = 64;
    public const long MaxTorrentFileBytes = 4L * 1024 * 1024;
    private const string BtihPrefix = "xt=urn:btih:";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static GameMagnetLink ValidateMagnet(string? text)
    {
        const string bad = "Enter a valid magnet link (magnet:?xt=urn:btih:...).";
        if (string.IsNullOrWhiteSpace(text)) throw new GameDownloadException(bad);
        string t = text.Trim();
        if (t.Length > MaxMagnetLength || t.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c > 126))
            throw new GameDownloadException(bad);
        if (!t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) throw new GameDownloadException(bad);
        string query = t["magnet:?".Length..];
        if (query.Contains('#')) throw new GameDownloadException(bad);
        string[] parts = query.Split('&');
        if (parts.Length > MaxMagnetParameters) throw new GameDownloadException(bad);
        // The info-hash must come first: strict "magnet:?xt=urn:btih:<hash>" shape.
        if (!parts[0].StartsWith(BtihPrefix, StringComparison.OrdinalIgnoreCase)) throw new GameDownloadException(bad);
        string hashText = parts[0][BtihPrefix.Length..];
        string hex = hashText.Length switch
        {
            40 when hashText.All(Uri.IsHexDigit) => hashText.ToLowerInvariant(),
            32 when hashText.ToUpperInvariant().All(c => Base32Alphabet.Contains(c)) => Base32ToHex(hashText.ToUpperInvariant()),
            _ => throw new GameDownloadException(bad),
        };
        string? displayName = null;
        for (int i = 1; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0) throw new GameDownloadException(bad);
            string key = parts[i][..eq].ToLowerInvariant();
            string value;
            try { value = Uri.UnescapeDataString(parts[i][(eq + 1)..].Replace('+', ' ')); }
            catch (UriFormatException) { throw new GameDownloadException(bad); }
            if (value.Any(char.IsControl)) throw new GameDownloadException(bad);
            if (key == "xt") throw new GameDownloadException(bad); // one content hash only
            if (key == "dn") { displayName ??= value; continue; }
            // Any address-like parameter (trackers, web seeds, exact sources) must not carry credentials.
            if (key is "tr" or "xs" or "as" or "ws" || key.StartsWith("tr.", StringComparison.Ordinal))
            {
                if (value.Contains('@')) throw new GameDownloadException("Links with embedded credentials are not accepted.");
                if (Uri.TryCreate(value, UriKind.Absolute, out Uri? u))
                {
                    if (!string.IsNullOrEmpty(u.UserInfo)) throw new GameDownloadException("Links with embedded credentials are not accepted.");
                    if (u.Scheme is not ("http" or "https" or "udp" or "ws" or "wss"))
                        throw new GameDownloadException(bad);
                }
            }
        }
        return new GameMagnetLink("magnet:?" + query, hex, displayName);
    }

    public static GameTorrentFile ValidateTorrentFile(string? path)
    {
        const string bad = "Choose a valid .torrent file on a local drive.";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new GameDownloadException(bad);
        string full = Path.GetFullPath(path.Trim());
        if (!string.Equals(Path.GetExtension(full), ".torrent", StringComparison.OrdinalIgnoreCase))
            throw new GameDownloadException(bad);
        byte[] data;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new GameDownloadException(bad);
            if (info.Length == 0 || info.Length > MaxTorrentFileBytes)
                throw new GameDownloadException("The .torrent file is empty or larger than 4 MB.");
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            data = new byte[(int)info.Length];
            fs.ReadExactly(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameDownloadException("The .torrent file could not be read.");
        }
        (string name, string hash, long length) = ParseTorrent(data);
        return new GameTorrentFile(full, name, hash, length);
    }

    /// <summary>Re-reads the file at transfer time and returns its bytes only if it is still a sane torrent with the same hash.</summary>
    public static byte[] ReadValidatedTorrentBytes(string fullPath, string expectedInfoHash)
    {
        GameTorrentFile again = ValidateTorrentFile(fullPath);
        if (!string.Equals(again.InfoHashHex, expectedInfoHash, StringComparison.Ordinal))
            throw new GameDownloadException("The .torrent file changed since it was selected.");
        return File.ReadAllBytes(again.FullPath);
    }

    // ---------------------------------------------------------------- bencode

    private sealed class Cursor
    {
        public byte[] Data = Array.Empty<byte>();
        public int Pos;
        public int Nodes;
    }

    private const int MaxDepth = 24;
    private const int MaxNodes = 200_000;

    /// <summary>Parses just enough bencode to prove the file is a single well-formed torrent and to hash its info dictionary.</summary>
    internal static (string Name, string InfoHash, long Length) ParseTorrent(byte[] data)
    {
        const string bad = "The file is not a valid .torrent (bencode check failed).";
        var c = new Cursor { Data = data };
        int infoStart = -1, infoEnd = -1;
        string? name = null;
        long length = 0;
        bool hasLengthOrFiles = false, piecesOk = false;
        try
        {
            if (c.Pos >= data.Length || data[c.Pos] != (byte)'d') throw new GameDownloadException(bad);
            c.Pos++;
            while (true)
            {
                if (c.Pos >= data.Length) throw new GameDownloadException(bad);
                if (data[c.Pos] == (byte)'e') { c.Pos++; break; }
                string key = Encoding.Latin1.GetString(ReadString(c));
                if (key == "info")
                {
                    infoStart = c.Pos;
                    if (c.Pos >= data.Length || data[c.Pos] != (byte)'d') throw new GameDownloadException(bad);
                    c.Pos++;
                    while (true)
                    {
                        if (c.Pos >= data.Length) throw new GameDownloadException(bad);
                        if (data[c.Pos] == (byte)'e') { c.Pos++; break; }
                        string ik = Encoding.Latin1.GetString(ReadString(c));
                        switch (ik)
                        {
                            case "name": name = Encoding.UTF8.GetString(ReadString(c)); break;
                            case "length": length = ReadInt(c); hasLengthOrFiles = true; break;
                            case "files":
                                hasLengthOrFiles = true;
                                SkipValue(c, 2);
                                break;
                            case "pieces":
                                { byte[] pieces = ReadString(c); piecesOk = pieces.Length > 0 && pieces.Length % 20 == 0; }
                                break;
                            default: SkipValue(c, 2); break;
                        }
                    }
                    infoEnd = c.Pos;
                }
                else SkipValue(c, 1);
            }
            if (c.Pos != data.Length) throw new GameDownloadException(bad); // trailing garbage
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or FormatException)
        {
            throw new GameDownloadException(bad);
        }
        if (infoStart < 0 || name is null || name.Length == 0 || !hasLengthOrFiles || !piecesOk || length < 0)
            throw new GameDownloadException(bad);
        string hash = Convert.ToHexString(SHA1.HashData(data.AsSpan(infoStart, infoEnd - infoStart))).ToLowerInvariant();
        return (name, hash, length);
    }

    private static byte[] ReadString(Cursor c)
    {
        if (++c.Nodes > MaxNodes) throw new ArgumentException("too large");
        int colon = Array.IndexOf(c.Data, (byte)':', c.Pos);
        if (colon <= c.Pos || colon - c.Pos > 10) throw new FormatException();
        string lenText = Encoding.ASCII.GetString(c.Data, c.Pos, colon - c.Pos);
        if (!lenText.All(char.IsAsciiDigit) || (lenText.Length > 1 && lenText[0] == '0')) throw new FormatException();
        long len = long.Parse(lenText);
        if (len > c.Data.Length - colon - 1) throw new FormatException();
        var result = c.Data.AsSpan(colon + 1, (int)len).ToArray();
        c.Pos = colon + 1 + (int)len;
        return result;
    }

    private static long ReadInt(Cursor c)
    {
        if (++c.Nodes > MaxNodes) throw new ArgumentException("too large");
        if (c.Data[c.Pos] != (byte)'i') throw new FormatException();
        int end = Array.IndexOf(c.Data, (byte)'e', c.Pos);
        if (end < 0 || end - c.Pos - 1 is < 1 or > 19) throw new FormatException();
        string t = Encoding.ASCII.GetString(c.Data, c.Pos + 1, end - c.Pos - 1);
        if (t == "-0" || (t.Length > 1 && (t[0] == '0' || t.StartsWith("-0", StringComparison.Ordinal)))) throw new FormatException();
        if (!(t.Skip(t[0] == '-' ? 1 : 0).All(char.IsAsciiDigit))) throw new FormatException();
        c.Pos = end + 1;
        return long.Parse(t);
    }

    private static void SkipValue(Cursor c, int depth)
    {
        if (depth > MaxDepth) throw new ArgumentException("too deep");
        if (c.Pos >= c.Data.Length) throw new FormatException();
        byte b = c.Data[c.Pos];
        if (b == (byte)'i') { ReadInt(c); return; }
        if (b >= (byte)'0' && b <= (byte)'9') { ReadString(c); return; }
        if (b == (byte)'l')
        {
            c.Pos++;
            while (true)
            {
                if (c.Pos >= c.Data.Length) throw new FormatException();
                if (c.Data[c.Pos] == (byte)'e') { c.Pos++; return; }
                SkipValue(c, depth + 1);
            }
        }
        if (b == (byte)'d')
        {
            c.Pos++;
            while (true)
            {
                if (c.Pos >= c.Data.Length) throw new FormatException();
                if (c.Data[c.Pos] == (byte)'e') { c.Pos++; return; }
                ReadString(c);
                SkipValue(c, depth + 1);
            }
        }
        throw new FormatException();
    }

    private static string Base32ToHex(string base32)
    {
        var bytes = new byte[20];
        int bits = 0, buffer = 0, index = 0;
        foreach (char ch in base32)
        {
            buffer = (buffer << 5) | Base32Alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes[index++] = (byte)((buffer >> bits) & 0xFF); }
        }
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
