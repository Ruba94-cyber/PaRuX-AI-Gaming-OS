using System.Security.Cryptography;

namespace PaRuX.Server;

/// <summary>
/// Download of one PaRuX-managed artifact whose identity PaRuX pins (SEC-F02/F12/F13): the SHA-256 and byte count are
/// mandatory, the bytes are written to a sibling partial file and published only after both match. A missing or
/// malformed digest fails closed before any request is sent.
/// </summary>
internal static class VerifiedDownload
{
    internal static bool IsValidSha256(string? value) =>
        value is { Length: 64 } && value.All(c => Uri.IsHexDigit(c));

    /// <summary>File extensions that are Python pickle containers and can execute code when loaded.</summary>
    internal static bool IsPickleFormat(string? fileName) =>
        fileName != null && new[] { ".pt", ".pth", ".ckpt", ".pkl", ".pickle", ".bin" }
            .Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    internal static async Task DownloadAsync(
        HttpClient http, string url, string destination, string? expectedSha256, long expectedBytes, CancellationToken ct)
    {
        if (!IsValidSha256(expectedSha256))
            throw new InvalidDataException("A managed download needs a pinned SHA-256; refusing to fetch " + Path.GetFileName(destination) + ".");
        if (expectedBytes <= 0)
            throw new InvalidDataException("A managed download needs a pinned size; refusing to fetch " + Path.GetFileName(destination) + ".");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("A managed download must use HTTPS.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + ".partial";
        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared != expectedBytes)
                throw new InvalidDataException("The download size does not match the pinned size.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > expectedBytes)
                        throw new InvalidDataException("The download is larger than its pinned size.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (total != expectedBytes)
                throw new InvalidDataException("The download is smaller than its pinned size.");
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expectedSha256!.ToLowerInvariant())))
                throw new InvalidDataException("The download does not match its pinned SHA-256.");
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
