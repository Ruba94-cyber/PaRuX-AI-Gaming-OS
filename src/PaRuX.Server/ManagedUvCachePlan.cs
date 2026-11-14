using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// uv extracts a source distribution inside its cache (<c>sdists-v9\pypi\&lt;name&gt;\&lt;version&gt;\&lt;id&gt;\src</c>) and the
/// MSVC linker (link.exe) cannot open an output path of 260 characters or more, even with Windows long paths enabled.
/// Measured on a real run: insightface's <c>build\lib.win-amd64-cpython-311\...\mesh_core_cython.cp311-win_amd64.pyd</c> was
/// 265 characters under a 100-character cache root and failed with LNK1104; the same build passed under a short cache.
/// The cache root therefore has to be short. A data root that is too deep gets a short PARUX_MANAGED cache beside the
/// per-user PaRuX folder; a portable data root never leaves its folder, so there the install is refused honestly instead.
/// </summary>
internal static partial class ManagedUvCachePlan
{
    /// <summary>
    /// Characters that uv's cache layout and the longest known linker output add below the cache root: 54 for
    /// <c>\sdists-v9\pypi\insightface\0.7.3\&lt;16-char id&gt;\src</c> plus 120 for the <c>.lib</c> path of the deepest insightface
    /// extension, which is the longest path the build writes (174 in total; MAX_PATH leaves 259).
    /// </summary>
    internal const int KnownBuildSuffixLength = 174;
    private const int MaxPath = 259;
    /// <summary>Largest cache root that still leaves a 5 character margin for slightly longer package names or versions.</summary>
    internal const int MaxCacheRootLength = MaxPath - KnownBuildSuffixLength - 5;

    /// <summary>Stable machine-readable code carried by engine status so each UI localizes the text itself.</summary>
    internal const string Code = "data_path_too_long";

    /// <summary>
    /// cl.exe cannot open headers at 260 characters or more either. The managed Python's deepest header adds 82 characters
    /// (<c>\cpython-3.11.9-windows-x86_64-none\Include\internal\pycore_blocks_output_buffer.h</c>); the real run failed with
    /// C1083 on <c>Include\cpython\bytearrayobject.h</c> under a 164-character data root.
    /// </summary>
    internal const int MaxPythonRootLength = MaxPath - 82 - 10;

    /// <summary>
    /// A venv is also used at run time: Windows cannot load a DLL or extension module whose path is 260 characters or more
    /// (measured: "DLL load failed ... Filename or extension too long" for onnxruntime_pybind11_state.pyd in an avatar venv under a
    /// 164-character data root). The longest known file below a venv root is
    /// <c>\Lib\site-packages\onnxruntime\capi\onnxruntime_pybind11_state.pyd</c> (66 characters); 14 more are kept as margin.
    /// A venv that cannot meet this is refused before anything is downloaded.
    /// </summary>
    internal const int MaxVenvRootLength = MaxPath - 66 - 14;

    internal static void RequireVenvPathBudget(string venvRoot)
    {
        if (venvRoot.Length > MaxVenvRootLength)
            throw new ManagedDataPathTooLongException(
                "Environment folder (" + venvRoot.Length + " characters, limit " + MaxVenvRootLength + "): " + venvRoot);
    }

    internal sealed record Plan(string CacheRoot, string PythonRoot, bool WithinBudget, bool Relocated);

    // LNK1104/LNK1181 (cannot open the output), "path too long" style messages from Python and the MSVC tools.
    [GeneratedRegex(@"LNK1104|LNK1181|WinError 206|filename or extension is too long|path (is )?too long", RegexOptions.IgnoreCase)]
    private static partial Regex PathLimitPattern();

    internal static bool LooksLikePathLimitFailure(string? log) => !string.IsNullOrEmpty(log) && PathLimitPattern().IsMatch(log);

    /// <summary>
    /// Chooses the uv cache root. The data root's own cache is kept whenever it fits the budget. Otherwise a short folder
    /// under <paramref name="localAppData"/> (named by a hash of the data root so two data roots never share a cache) is used,
    /// except for a portable data root, which must not write outside itself.
    /// </summary>
    internal static Plan Resolve(string dataCacheRoot, string dataPythonRoot, string dataRoot, string? portableRoot, string? localAppData)
    {
        if (dataCacheRoot.Length <= MaxCacheRootLength && dataPythonRoot.Length <= MaxPythonRootLength)
            return new Plan(dataCacheRoot, dataPythonRoot, true, false);
        bool portable = portableRoot != null && IsSameOrInside(dataRoot, portableRoot);
        if (!portable && !string.IsNullOrWhiteSpace(localAppData))
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToLowerInvariant())))[..8].ToLowerInvariant();
            string cache = Path.Combine(localAppData, "PaRuX", "uvc", key);
            string python = Path.Combine(localAppData, "PaRuX", "uvp", key);
            if (cache.Length <= MaxCacheRootLength && python.Length <= MaxPythonRootLength)
                return new Plan(cache, python, true, true);
        }
        return new Plan(dataCacheRoot, dataPythonRoot, false, false);
    }

    /// <summary>Marks a relocated cache as PaRuX-owned so nobody mistakes it for stray data.</summary>
    internal static void WriteOwnerMarker(Plan plan, string dataRoot)
    {
        if (!plan.Relocated) return;
        try
        {
            foreach (string folder in new[] { plan.CacheRoot, plan.PythonRoot })
            {
                Directory.CreateDirectory(folder);
                string marker = Path.Combine(folder, "PARUX_MANAGED.txt");
                if (!File.Exists(marker))
                    File.WriteAllText(marker, "PARUX_MANAGED uv cache / managed Python for the PaRuX data root:\n" + dataRoot
                        + "\nKept short on purpose: the C++ compiler and linker cannot open paths of 260 characters or more.\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsSameOrInside(string path, string root)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string prefix = Path.GetFullPath(root).TrimEnd('\\', '/');
            return full.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>
/// A managed source build failed because the data folder is too deep for the C++ linker and no short PARUX_MANAGED cache
/// could be used. Typed so the setup status shows an honest, localized state instead of a linker log.
/// </summary>
internal sealed class ManagedDataPathTooLongException(string detail)
    : InvalidOperationException(
        "The PaRuX data folder path is too long for this engine: Windows and the C++ tools cannot open file paths of 260 characters or more. "
        + "Move the PaRuX data to a shorter folder, then retry."
        + (string.IsNullOrWhiteSpace(detail) ? string.Empty : "\n" + detail));

/// <summary>Maps a managed-install exception to the stable status code the UI localizes, or null for an untyped failure.</summary>
internal static class ManagedInstallFailureCode
{
    internal static string? Of(Exception ex) => ex switch
    {
        ManagedBuildToolsRequiredException => ManagedBuildToolsFailure.Code,
        ManagedDataPathTooLongException => ManagedUvCachePlan.Code,
        _ => null
    };
}
