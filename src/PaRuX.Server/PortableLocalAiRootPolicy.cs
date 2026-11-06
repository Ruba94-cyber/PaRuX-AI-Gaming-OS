namespace PaRuX.Server;

/// <summary>
/// Chooses the default LocalAI storage without accidentally adopting host Documents
/// when the portable launcher supplied an isolated, fully qualified data root.
/// An explicitly saved LocalAI root is resolved by callers before this fallback.
/// Pure: no environment or filesystem access, so it is safe to test in parallel.
/// </summary>
internal static class PortableLocalAiRootPolicy
{
    internal static string Resolve(
        string dataRoot,
        string? explicitDataRoot,
        string legacyLocalAi,
        bool legacyPopulated)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataRoot) &&
            Path.IsPathFullyQualified(explicitDataRoot))
            return Path.Combine(dataRoot, "LocalAI");

        return legacyPopulated ? legacyLocalAi : Path.Combine(dataRoot, "LocalAI");
    }
}
