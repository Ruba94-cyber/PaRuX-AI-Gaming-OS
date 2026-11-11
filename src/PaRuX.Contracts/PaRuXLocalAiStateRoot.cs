namespace PaRuX.Contracts;

/// <summary>
/// Resolves the state directory used to coordinate PaRuX with its LocalAI runtimes.
/// </summary>
public static class PaRuXLocalAiStateRoot
{
    private const string OverrideEnvironmentVariable = "PARUX_LOCALAI_STATE_DIR";

    /// <summary>
    /// %LOCALAPPDATA%\LocalAI by default. A process-local absolute override is supported for
    /// isolated integration/UI-test child processes; normal launches never set it.
    /// </summary>
    public static string Path => Resolve(AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable(OverrideEnvironmentVariable), PackageIdentity.FamilyName);

    /// <summary>Portable marker wins over inherited process overrides; all
    /// coordination files stay inside the same movable PaRuXData directory.</summary>
    internal static string Resolve(string baseDirectory, string? overrideRoot, string? packageFamilyName = null)
    {
        string? portableData = PaRuXDataRoot.FindPortableDataRoot(baseDirectory);
        if (portableData != null) return portableData;
        if (!string.IsNullOrWhiteSpace(overrideRoot) && System.IO.Path.IsPathFullyQualified(overrideRoot))
            return System.IO.Path.GetFullPath(overrideRoot);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (PackageIdentity.IsValidFamilyName(packageFamilyName))
            return PackageIdentity.PackagedLocalAiStateRoot(localAppData, packageFamilyName!);
        return System.IO.Path.Combine(localAppData, "LocalAI");
    }

}
