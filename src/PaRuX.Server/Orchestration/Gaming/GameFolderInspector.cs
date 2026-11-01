using System.Text.RegularExpressions;

namespace PaRuX.Server.Orchestration.Gaming;

public sealed record ExecutableCandidate(string RelativePath, long SizeBytes, int Score, string Reason);

public sealed record GameFolderInspection(
    string FolderPath, string SuggestedTitle, IReadOnlyList<ExecutableCandidate> Candidates, bool Truncated, string? Error);

/// <summary>
/// "Import an installed / portable game": looks for the likely main executable in a folder the user chose
/// and proposes a title. It only reads directory entries (never runs, opens or hashes anything), does not
/// follow junctions or symlinks, and is bounded in depth and file count. Nothing is added to the library
/// here; the user confirms a candidate first.
/// </summary>
public static class GameFolderInspector
{
    public const int MaxDepth = 4;
    public const int MaxEntries = 5000;
    public const int MaxCandidates = 25;

    private static readonly string[] RejectedNameParts =
    {
        "unins", "uninstall", "setup", "install", "vc_redist", "vcredist", "dxsetup", "dotnet", "dxwebsetup",
        "crashreport", "crashhandler", "unitycrashhandler", "updater", "redist", "oalinst", "physx", "notification_helper",
    };

    private static readonly HashSet<string> RejectedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "_redist", "redist", "_commonredist", "commonredist", "directx", "vcredist", "dotnetfx", "support", "__installer",
        "installer", "crashreporter", "tools", "docs", "manual",
    };

    private static readonly Regex BracketGroups = new(@"\s*[\[\(][^\]\)]*[\]\)]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex VersionTail = new(
        @"[\s._-]+(v|ver|version|build)[\s._-]*\d[\w.\-]*\s*$|[\s._-]+\d+(\.\d+){1,3}\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static GameFolderInspection Inspect(string? folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 1024 || folder.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            return Failure(folder, "Choose an absolute local folder.");
        string root;
        try { root = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return Failure(folder, "The folder path is not valid."); }
        if (!Directory.Exists(root)) return Failure(root, "The folder does not exist.");
        if (IsLink(root)) return Failure(root, "Linked folders are not imported.");

        var candidates = new List<ExecutableCandidate>();
        int entries = 0;
        bool truncated = false;
        string folderKey = Normalize(new DirectoryInfo(root).Name);

        void Walk(string directory, int depth)
        {
            if (truncated) return;
            IEnumerable<string> files, subdirectories;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).ToArray();
                subdirectories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) { truncated = true; return; }
                if (IsLink(file)) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (RejectedNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;
                long size;
                try { size = new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (size < 10_000) continue; // stubs and shortcuts are not games
                (int score, string reason) = Score(name, folderKey, depth, size);
                candidates.Add(new ExecutableCandidate(Path.GetRelativePath(root, file).Replace('\\', '/'), size, score, reason));
            }

            if (depth >= MaxDepth) return;
            foreach (string sub in subdirectories)
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaxEntries) { truncated = true; return; }
                if (IsLink(sub) || RejectedFolders.Contains(Path.GetFileName(sub))) continue;
                Walk(sub, depth + 1);
            }
        }

        Walk(root, 0);
        var ranked = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates).ToArray();
        return new GameFolderInspection(root, CleanTitle(new DirectoryInfo(root).Name), ranked, truncated,
            ranked.Length == 0 ? "No likely game executable was found in this folder." : null);
    }

    private static (int Score, string Reason) Score(string name, string folderKey, int depth, long size)
    {
        int score = 50 - 6 * depth;
        var reasons = new List<string>();
        string key = Normalize(name);
        if (key.Length > 0 && folderKey.Length > 0)
        {
            if (key == folderKey) { score += 30; reasons.Add("name matches the folder"); }
            else if (folderKey.Contains(key) || key.Contains(folderKey)) { score += 15; reasons.Add("name resembles the folder"); }
        }
        if (depth == 0) reasons.Add("top level");
        int sizeBonus = (int)Math.Min(15, Math.Log2(Math.Max(1, size / (1024.0 * 1024.0))) * 2);
        score += sizeBonus;
        if (sizeBonus >= 8) reasons.Add("large executable");
        if (name.Contains("64", StringComparison.Ordinal)) { score += 2; reasons.Add("64-bit"); }
        if (name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) { score += 8; reasons.Add("Unreal shipping binary"); }
        return (score, reasons.Count == 0 ? "executable" : string.Join(", ", reasons));
    }

    /// <summary>Folder name to a readable title: no brackets, no trailing version, separators to spaces.</summary>
    public static string CleanTitle(string folderName)
    {
        string title = folderName;
        try
        {
            title = BracketGroups.Replace(title, string.Empty);
            title = VersionTail.Replace(title, string.Empty); // before separators change: "v1.2.3" is still intact here
            if (!title.Contains(' ')) title = title.Replace('_', ' ').Replace('.', ' ');
        }
        catch (RegexMatchTimeoutException) { title = folderName; }
        title = Regex.Replace(title.Trim(' ', '-', '_', '.'), @"\s{2,}", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
        return title.Length == 0 ? folderName : (title.Length > 200 ? title[..200] : title);
    }

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static GameFolderInspection Failure(string? folder, string error) =>
        new(folder ?? string.Empty, string.Empty, Array.Empty<ExecutableCandidate>(), false, error);
}
