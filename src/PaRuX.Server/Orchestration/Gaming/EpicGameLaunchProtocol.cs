using Microsoft.Win32;
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
        !string.IsNullOrWhiteSpace(id) && id.Length <= 200 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

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
            if (!Directory.Exists(normalized) ||
                (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
                return false;

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
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(Scheme, writable: false);
            // The existence of this registration is an OS dispatch prerequisite,
            // NOT attestation that the registered executable is publisher-signed.
            return key != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            SecurityException) { return false; }
    }
}
