namespace PaRuX.Contracts;

/// <summary>
/// "Corporate Lite Offline Portable" is the same product started in a restricted profile: AI only, no Gaming, no cloud,
/// no automatic updates, no runtime downloads and offline by default. A separately consented model-only download session\n/// may fetch GGUF weights without enabling general web or cloud inference. Everything lives beside the program. It is switched on by the launcher of
/// the Corporate package (environment variable <c>PARUX_CORPORATE=1</c>) or by a <c>parux.corporate</c> marker file next to
/// the executable; the standard MSI and portable builds never set either, so their behaviour is unchanged.
/// </summary>
public static class CorporateMode
{
    public const string EnvironmentVariable = "PARUX_CORPORATE";
    public const string ModelDownloadsEnvironmentVariable = "PARUX_CORPORATE_MODEL_DOWNLOADS";
    public const string MarkerFileName = "parux.corporate";

    public static bool IsActive => Evaluate(Environment.GetEnvironmentVariable(EnvironmentVariable), AppContext.BaseDirectory);

    /// <summary>True only for a Corporate session explicitly launched with model downloads enabled.
    /// This is intentionally not the global Internet toggle: Gaming, automatic updates, cloud
    /// inference and arbitrary web browsing stay disabled. The default launcher never enables it.</summary>
    public static bool ModelDownloadsAllowed => AreModelDownloadsAllowed(
        IsActive, Environment.GetEnvironmentVariable(ModelDownloadsEnvironmentVariable));

    /// <summary>Pure consent gate; a standard install never inherits the Corporate permission.</summary>
    public static bool AreModelDownloadsAllowed(bool isCorporate, string? requested) =>
        isCorporate && (string.Equals(requested?.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(requested?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pure rule, testable without touching the process environment.</summary>
    public static bool Evaluate(string? environmentValue, string baseDirectory)
    {
        if (string.Equals(environmentValue?.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(environmentValue?.Trim(), "true", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            // The Core runs from a PaRuX.Server subfolder: the marker may sit one level above.
            foreach (string dir in new[] { baseDirectory, Path.GetDirectoryName(baseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? baseDirectory })
                if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        }
        catch (Exception) { /* unreadable directory: not corporate */ }
        return false;
    }
}
