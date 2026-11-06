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
    internal static string DefaultLocation()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("PARUX_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot) && System.IO.Path.IsPathFullyQualified(overrideRoot))
            return System.IO.Path.GetFullPath(overrideRoot);

        // Double-clicking PaRuX.exe inside an extracted Corporate ZIP must be portable
        // as well: the launcher is helpful for consent but not a prerequisite for finding
        // the preseeded CPU runtime, model and document index. Never apply this to MSI/
        // standard portable editions without the explicit corporate marker.
        string? corporateRoot = FindCorporatePortableDataRoot(AppContext.BaseDirectory);
        if (corporateRoot != null) return corporateRoot;

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
    }

    /// <summary>Pure directory boundary for tests and normal executable/server startup.
    /// The marker must sit adjacent to a real PaRuXData directory. Environment-only
    /// corporate UI tests do not accidentally inherit a sibling directory.</summary>
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
