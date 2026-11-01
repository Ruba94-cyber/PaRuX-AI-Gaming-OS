using System.Text.RegularExpressions;

namespace PaRuX.Contracts;

public enum PatchOperation { Modify, Create, Delete }

/// <param name="Ops">The hunk body in order: ' ' context, '-' removed, '+' added.</param>
public sealed record PatchHunk(
    int OldStart, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines, bool OldNoEol, bool NewNoEol,
    IReadOnlyList<(char Kind, string Text)> Ops);

public sealed record FilePatch(string Path, PatchOperation Operation, IReadOnlyList<PatchHunk> Hunks);

public sealed record PatchParseResult(IReadOnlyList<FilePatch> Files, string? Error);

/// <param name="NewContent">Null for a deleted file or when this file failed.</param>
public sealed record PatchFileResult(string Path, PatchOperation Operation, string? NewContent, string? Error);

public sealed record PatchApplyResult(bool Success, IReadOnlyList<PatchFileResult> Files, string? Error);

/// <summary>
/// Pure unified-diff parser and applier used by the coding agent's apply_patch action. It never touches the
/// disk: callers supply file contents and receive the full set of new contents, or a single failure with
/// nothing applied (all-or-nothing). Paths must be plain workspace-relative forward-slash paths; renames,
/// binary patches and mode changes are rejected rather than guessed.
/// </summary>
public static class UnifiedDiffPatch
{
    public const int MaxDiffChars = 1_000_000;
    public const int MaxFiles = 50;
    public const int MaxHunks = 500;

    private static readonly Regex HunkHeader = new(
        @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260) return false;
        if (path.Contains(':') || path.Contains('\\') || path[0] == '/' || path.Any(char.IsControl)) return false;
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            // Windows device names (also with an extension, e.g. "nul.txt") would address a device, not a file.
            int dot = segment.IndexOf('.');
            if (ReservedWindowsNames.Contains(dot >= 0 ? segment[..dot] : segment)) return false;
        }
        return true;
    }

    public static PatchParseResult Parse(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff)) return Fail("The patch is empty.");
        if (diff.Length > MaxDiffChars) return Fail("The patch is too large.");
        string[] lines = diff.Replace("\r\n", "\n").Split('\n');
        var files = new List<FilePatch>();
        int totalHunks = 0;
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (line.StartsWith("GIT binary patch", StringComparison.Ordinal) || line.StartsWith("Binary files ", StringComparison.Ordinal))
                return Fail("Binary patches are not supported.");
            if (line.StartsWith("rename from ", StringComparison.Ordinal) || line.StartsWith("rename to ", StringComparison.Ordinal))
                return Fail("Renames are not supported; delete and create the file instead.");
            if (line.StartsWith("old mode ", StringComparison.Ordinal) || line.StartsWith("new mode ", StringComparison.Ordinal))
                return Fail("File mode changes are not supported.");
            if (!line.StartsWith("--- ", StringComparison.Ordinal)) { i++; continue; }
            if (i + 1 >= lines.Length || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                return Fail("A '---' header must be followed by a '+++' header.");
            if (files.Count >= MaxFiles) return Fail("The patch touches too many files.");

            string oldPath = HeaderPath(line), newPath = HeaderPath(lines[i + 1]);
            PatchOperation operation;
            string path;
            if (oldPath == "/dev/null") { operation = PatchOperation.Create; path = StripPrefix(newPath); }
            else if (newPath == "/dev/null") { operation = PatchOperation.Delete; path = StripPrefix(oldPath); }
            else
            {
                path = StripPrefix(newPath);
                if (StripPrefix(oldPath) != path) return Fail("Renames are not supported.");
                operation = PatchOperation.Modify;
            }
            if (!IsSafeRelativePath(path)) return Fail($"Unsafe patch path '{Truncate(path)}'.");
            i += 2;

            var hunks = new List<PatchHunk>();
            while (i < lines.Length && lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                if (++totalHunks > MaxHunks) return Fail("The patch has too many hunks.");
                Match header;
                try { header = HunkHeader.Match(lines[i]); }
                catch (RegexMatchTimeoutException) { return Fail("A hunk header could not be parsed."); }
                if (!header.Success) return Fail("Malformed hunk header.");
                int oldStart = int.Parse(header.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                int oldCount = header.Groups[2].Success ? int.Parse(header.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                int newCount = header.Groups[4].Success ? int.Parse(header.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
                if (oldCount > 100_000 || newCount > 100_000) return Fail("A hunk is too large.");
                i++;

                var oldLines = new List<string>();
                var newLines = new List<string>();
                var ops = new List<(char, string)>();
                bool oldNoEol = false, newNoEol = false;
                char lastKind = ' ';
                while (i < lines.Length && (oldLines.Count < oldCount || newLines.Count < newCount || lines[i].StartsWith('\\')))
                {
                    string body = lines[i];
                    if (body.StartsWith('\\'))
                    {
                        // "\ No newline at end of file" applies to the side of the line just above it.
                        if (lastKind != '+') oldNoEol = true;
                        if (lastKind != '-') newNoEol = true;
                        i++;
                        continue;
                    }
                    // A blank line is a context line whose leading space was stripped by an editor or model.
                    char kind = body.Length == 0 ? ' ' : body[0];
                    string text = body.Length == 0 ? string.Empty : body[1..];
                    switch (kind)
                    {
                        case ' ': oldLines.Add(text); newLines.Add(text); break;
                        case '-': oldLines.Add(text); break;
                        case '+': newLines.Add(text); break;
                        default: return Fail("A hunk line must start with ' ', '+', '-' or '\\'.");
                    }
                    ops.Add((kind, text));
                    lastKind = kind;
                    i++;
                }
                if (oldLines.Count != oldCount || newLines.Count != newCount)
                    return Fail("A hunk body does not match its header line counts.");
                hunks.Add(new PatchHunk(oldStart, oldLines, newLines, oldNoEol, newNoEol, ops));
            }
            if (hunks.Count == 0) return Fail($"No hunks for '{Truncate(path)}'.");
            if (operation == PatchOperation.Create && (hunks.Count != 1 || hunks[0].OldLines.Count != 0))
                return Fail("A new file must have exactly one hunk with only added lines.");
            if (operation == PatchOperation.Delete && (hunks.Count != 1 || hunks[0].NewLines.Count != 0))
                return Fail("A deleted file must have exactly one hunk with only removed lines.");
            if (files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                return Fail($"The patch lists '{Truncate(path)}' more than once.");
            files.Add(new FilePatch(path, operation, hunks));
        }
        return files.Count == 0 ? Fail("No file changes were found in the patch.") : new PatchParseResult(files, null);
    }

    /// <summary>All-or-nothing: either every file applies and its new content is returned, or none does.</summary>
    public static PatchApplyResult Apply(IReadOnlyList<FilePatch> patches, Func<string, string?> readFile)
    {
        var results = new List<PatchFileResult>(patches.Count);
        foreach (FilePatch patch in patches)
        {
            string? existing = readFile(patch.Path);
            PatchFileResult result = patch.Operation switch
            {
                PatchOperation.Create => existing is not null
                    ? Failed(patch, "The file already exists.")
                    : new PatchFileResult(patch.Path, patch.Operation, Join(patch.Hunks[0].NewLines, "\n", !patch.Hunks[0].NewNoEol), null),
                PatchOperation.Delete => existing is null
                    ? Failed(patch, "The file does not exist.")
                    : MatchesWhole(existing, patch.Hunks[0].OldLines)
                        ? new PatchFileResult(patch.Path, patch.Operation, null, null)
                        : Failed(patch, "The file content differs from the patch; it was not deleted."),
                _ => existing is null ? Failed(patch, "The file does not exist.") : ApplyModify(patch, existing),
            };
            if (result.Error is not null)
                return new PatchApplyResult(false, new[] { result }, $"{patch.Path}: {result.Error} Nothing was applied.");
            results.Add(result);
        }
        return new PatchApplyResult(true, results, null);
    }

    private static PatchFileResult ApplyModify(FilePatch patch, string existing)
    {
        string eol = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsWithNewline = existing.EndsWith('\n');
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

        int shift = 0;
        int minIndex = 0;
        bool trailingNewline = endsWithNewline;
        foreach (PatchHunk hunk in patch.Hunks)
        {
            int index;
            if (hunk.OldLines.Count == 0)
            {
                index = Math.Clamp(hunk.OldStart + shift, minIndex, lines.Count);
            }
            else
            {
                int expected = hunk.OldStart - 1 + shift;
                index = Locate(lines, hunk.OldLines, expected, minIndex, lenient: false);
                if (index < 0) index = Locate(lines, hunk.OldLines, expected, minIndex, lenient: true);
                if (index < 0) return Failed(patch, $"A hunk near line {hunk.OldStart} does not match the file; the file may have changed.");
            }
            // Rebuild the region from the hunk's own operations, but keep the file's original text for context
            // lines: a whitespace-tolerant match must not rewrite lines the patch does not change.
            var replacement = new List<string>(hunk.NewLines.Count);
            int original = index;
            foreach ((char kind, string text) in hunk.Ops)
            {
                if (kind == ' ') replacement.Add(lines[original++]);
                else if (kind == '-') original++;
                else replacement.Add(text);
            }
            lines.RemoveRange(index, hunk.OldLines.Count);
            lines.InsertRange(index, replacement);
            shift = index - (hunk.OldStart - 1) + (hunk.NewLines.Count - hunk.OldLines.Count);
            if (hunk.OldLines.Count == 0) shift = index - hunk.OldStart + hunk.NewLines.Count;
            minIndex = index + hunk.NewLines.Count;
            if (minIndex >= lines.Count)
            {
                if (hunk.NewNoEol) trailingNewline = false;
                else if (hunk.OldNoEol) trailingNewline = true;
            }
        }
        string content = string.Join(eol, lines) + (trailingNewline && lines.Count > 0 ? eol : string.Empty);
        return new PatchFileResult(patch.Path, patch.Operation, content, null);
    }

    private static int Locate(List<string> lines, IReadOnlyList<string> needle, int expected, int minIndex, bool lenient)
    {
        int last = lines.Count - needle.Count;
        if (last < minIndex) return -1;
        expected = Math.Clamp(expected, minIndex, last);
        for (int distance = 0; distance <= lines.Count; distance++)
        {
            int below = expected - distance, above = expected + distance;
            if (below >= minIndex && Matches(lines, needle, below, lenient)) return below;
            if (distance > 0 && above <= last && Matches(lines, needle, above, lenient)) return above;
            if (below < minIndex && above > last) break;
        }
        return -1;
    }

    private static bool Matches(List<string> lines, IReadOnlyList<string> needle, int index, bool lenient)
    {
        for (int k = 0; k < needle.Count; k++)
        {
            string a = lines[index + k], b = needle[k];
            if (lenient ? !string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal) : !string.Equals(a, b, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool MatchesWhole(string existing, IReadOnlyList<string> expected)
    {
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        if (existing.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines.Count == expected.Count && Matches(lines, expected, 0, lenient: false);
    }

    private static string Join(IReadOnlyList<string> lines, string eol, bool finalNewline) =>
        string.Join(eol, lines) + (finalNewline && lines.Count > 0 ? eol : string.Empty);

    private static string HeaderPath(string header)
    {
        string value = header[4..];
        int tab = value.IndexOf('\t');
        return (tab >= 0 ? value[..tab] : value).Trim();
    }

    private static string StripPrefix(string path) =>
        path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;

    private static PatchFileResult Failed(FilePatch patch, string error) => new(patch.Path, patch.Operation, null, error);
    private static PatchParseResult Fail(string error) => new(Array.Empty<FilePatch>(), error);
    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "…";
}
