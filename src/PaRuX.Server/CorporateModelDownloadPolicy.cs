using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Corporate Lite only opts into model weights, never arbitrary downloads, media or runtime installers.
/// The main Core request/path validation, immutable revision, licence review and integrity rules still apply.</summary>
public static class CorporateModelDownloadPolicy
{
    public static bool IsTextGgufFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        string name = fileName.Trim();
        return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("projector", StringComparison.OrdinalIgnoreCase)
            && name == Path.GetFileName(name)
            && !name.Contains("..", StringComparison.Ordinal)
            && name.Length <= 240;
    }

    /// <summary>When resuming a saved job the client sends its id rather than a source URL.
    /// Only a previously admitted GGUF job may resume.</summary>
    public static bool IsTextGgufRequest(ModelDownloadRequest? request, Func<string, string?>? savedJobFileName = null)
    {
        if (request == null || !string.IsNullOrWhiteSpace(request.MediaCheckpointType)) return false;
        if (!string.IsNullOrWhiteSpace(request.ResumeFromDownloadId))
            return savedJobFileName != null
                && IsTextGgufFileName(savedJobFileName(request.ResumeFromDownloadId));
        return IsTextGgufFileName(request.FileName);
    }
}
