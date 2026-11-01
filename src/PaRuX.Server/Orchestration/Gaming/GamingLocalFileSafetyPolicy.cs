using System.Security;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// First-party, read-only admission of a regular file on a local volume.
/// This is path hygiene, not publisher verification or a race-free file-open primitive.
/// It intentionally does not execute files or authorize game installation.
/// </summary>
internal static class GamingLocalFileSafetyPolicy
{
    internal static readonly IReadOnlySet<string> ExecutableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe" };

    internal static bool IsSafeRegularFile(string? path, IReadOnlySet<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) ||
            !extensions.Contains(Path.GetExtension(path)))
            return false;

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return false;

            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) return false;
            DriveType type = new DriveInfo(root).DriveType;
            if (type is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                return false;

            for (string? dir = Path.GetDirectoryName(fullPath);
                 dir != null; dir = Path.GetDirectoryName(dir))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }
}
