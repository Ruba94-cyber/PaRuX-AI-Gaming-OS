namespace PaRuX.Contracts;

/// <summary>
/// The one place PaRuX's per-user data directory is defined.
///
/// Everything durable lives under it: conversations, memory, the RAG index, settings, paired-device
/// credentials, downloaded models and installed runtimes. Resolving it in one place is what keeps a
/// component from inventing its own location and stranding the user's data somewhere nothing else
/// reads.
/// </summary>
public static class PaRuXDataRoot
{
    private const string FolderName = "PaRuX";
    /// <summary>The standard Portable edition carries its data beside PaRuX.exe.</summary>
    public const string PortableMarkerFileName = "parux.portable";

    private static readonly object Gate = new();
    private static string? _resolved;

    /// <summary>
    /// %LOCALAPPDATA%\PaRuX by default. A process-local absolute override is supported for
    /// isolated integration/UI-test child processes; normal launches never set it.
    /// </summary>
    public static string Path
    {
        get
        {
            if (_resolved != null) return _resolved;
            lock (Gate)
            {
                _resolved ??= DefaultLocation();
                return _resolved;
            }
        }
    }

    public static string Combine(params string[] parts)
    {
        string[] all = new string[parts.Length + 1];
        all[0] = Path;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    /// <summary>Test seam: forces the next <see cref="Path"/> access to resolve again.</summary>
    internal static void ResetForTests() { lock (Gate) { _resolved = null; } }

    /// <summary>Test seam: points the whole process at an isolated directory. The test suite sets
    /// this before anything else runs, because several suites exercise the real stores and would
    /// otherwise create and mutate files inside the developer&apos;s own installation.</summary>
    internal static void UseForTests(string directory)
    {
        lock (Gate) { _resolved = directory; }
    }

    /// <summary>The location computed without touching the disk, so the naming rule can be
    /// asserted directly.</summary>
    internal static string DefaultLocation() => ResolveDefaultLocation(
        AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PARUX_DATA_ROOT"));

    /// <summary>Portable marker wins over inherited/machine-wide environment
    /// overrides, which must not silently redirect a USB edition into some
    /// unrelated host user's profile. Pure with respect to process environment.</summary>
    internal static string ResolveDefaultLocation(string baseDirectory, string? overrideRoot)
    {
        string? portableRoot = FindPortableDataRoot(baseDirectory);
        if (portableRoot != null) return portableRoot;

        if (!string.IsNullOrWhiteSpace(overrideRoot) && System.IO.Path.IsPathFullyQualified(overrideRoot))
            return System.IO.Path.GetFullPath(overrideRoot);

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
    }

    /// <summary>Pure directory boundary for tests and normal executable/server startup.
    /// The marker must sit adjacent to a real PaRuXData directory. Environment-only
    /// corporate UI tests do not accidentally inherit a sibling directory.</summary>
    /// <summary>Recognizes a physically extracted Standard/Corporate ZIP, from
    /// the Desktop exe folder or the nested PaRuX.Server Core directory. A marker
    /// alone is insufficient: PaRuXData must also exist beside it.</summary>
    public static string? FindPortableDataRoot(string baseDirectory)
    {
        try
        {
            string current = System.IO.Path.GetFullPath(baseDirectory);
            foreach (string root in new[] { current, System.IO.Path.GetDirectoryName(current.TrimEnd(
                System.IO.Path.DirectorySeparatorChar)) ?? current })
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(root, "PaRuXData")) &&
                    (System.IO.File.Exists(System.IO.Path.Combine(root, PortableMarkerFileName)) ||
                     System.IO.File.Exists(System.IO.Path.Combine(root, CorporateMode.MarkerFileName))))
                    return System.IO.Path.Combine(root, "PaRuXData");
            }
        }
        catch (Exception) { /* standard MSI/profile fallback */ }
        return null;
    }

    internal static string? FindCorporatePortableDataRoot(string baseDirectory)
    {
        try
        {
            string current = System.IO.Path.GetFullPath(baseDirectory);
            foreach (string root in new[] { current, System.IO.Path.GetDirectoryName(current.TrimEnd(
                System.IO.Path.DirectorySeparatorChar)) ?? current })
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(root, CorporateMode.MarkerFileName)))
                {
                    string data = System.IO.Path.Combine(root, "PaRuXData");
                    if (System.IO.Directory.Exists(data)) return data;
                }
            }
        }
        catch (Exception) { /* ordinary local profile fallback */ }
        return null;
    }
}
