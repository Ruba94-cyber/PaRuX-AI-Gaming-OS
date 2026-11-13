using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Reviewed SHA-256 pins for managed runtime release assets (SEC-F12). The digest GitHub advertises next to an asset
/// proves transport integrity only; a pin shipped inside the signed PaRuX build proves the bytes are the ones a
/// reviewer approved. When a pin exists for (owner/repo, asset name) the advertised digest must equal it, otherwise
/// the asset is refused. An asset without a pin keeps the existing GitHub-digest rule (documented residual).
/// </summary>
internal static class RuntimeDigestPins
{
    internal const string FileName = "runtime-digest-pins.json";

    /// <summary>Location of the pin file. Tests may point it at a fixture.</summary>
    internal static string Path { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The pinned digest (lowercase hex), or null when this asset has no pin.</summary>
    internal static string? Find(string owner, string repo, string assetName)
    {
        if (!File.Exists(Path)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path));
            if (!doc.RootElement.TryGetProperty("pins", out JsonElement pins) || pins.ValueKind != JsonValueKind.Array) return null;
            string repository = owner + "/" + repo;
            foreach (JsonElement pin in pins.EnumerateArray())
            {
                string? pinRepository = pin.TryGetProperty("repository", out JsonElement r) ? r.GetString() : null;
                string? asset = pin.TryGetProperty("asset", out JsonElement a) ? a.GetString() : null;
                string? sha = pin.TryGetProperty("sha256", out JsonElement s) ? s.GetString() : null;
                if (string.Equals(pinRepository, repository, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(asset, assetName, StringComparison.Ordinal)
                    && VerifiedDownload.IsValidSha256(sha))
                    return sha!.ToLowerInvariant();
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return null;
    }
}
