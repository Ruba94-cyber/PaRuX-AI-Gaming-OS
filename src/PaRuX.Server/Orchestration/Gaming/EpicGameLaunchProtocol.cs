using Microsoft.Win32;
using PaRuX.Contracts;
using System.Security;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// First-party, read-only construction of Epic's documented OS protocol handoff.
/// No executable path, URI, launch switches or local file path comes from HTTP.
/// The Windows-registered URI handler is an external, user-controlled component;
/// dispatch is NOT proof that Epic or the actual game started.
/// </summary>
internal static class EpicGameLaunchProtocol
{
    private const string Scheme = "com.epicgames.launcher";
    private const string Prefix = Scheme + "://apps/";

    internal static bool IsValidGameId(string? id) =>
        GameStoreLaunchIdPolicy.Epic(id);

    internal static bool TryBuildInstalledGameUri(string? installedDirectory, out string? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(installedDirectory) ||
            installedDirectory.Length > 1024 ||
            !Path.IsPathFullyQualified(installedDirectory) ||
            installedDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        try
        {
            string normalized = Path.GetFullPath(installedDirectory);
            if (!Directory.Exists(normalized)) return false;
            string? root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root)) return false;
            DriveType drive = new DriveInfo(root).DriveType;
            if (drive is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown)
                return false;

            // Install folders may themselves be ordinary directories inside a
            // junction pointing elsewhere. Reject every linked ancestor, as in
            // PaRuX's executable/installer preflight. Never dereference an
            // untrusted manifest path to construct the launcher protocol request.
            for (string? directory = normalized; directory != null;
                 directory = Path.GetDirectoryName(directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }

            // Epic's Protocol Activation guide explicitly supports a URL-encoded
            // *local install directory*, without requiring the namespace/catalog IDs.
            // Encoding is essential for spaces, percent, colon, ?, # and &.
            uri = Prefix + Uri.EscapeDataString(normalized) + "?action=launch&silent=true";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    internal static bool IsWindowsProtocolRegistered()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using RegistryKey? key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(Scheme, writable: false);
            // The existence of this registration is an OS dispatch prerequisite,
            // NOT attestation that the registered executable is publisher-signed.
            return key != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            SecurityException) { return false; }
    }
}
