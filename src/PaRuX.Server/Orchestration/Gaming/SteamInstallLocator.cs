using Microsoft.Win32;
using System.Security;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Read-only local Steam client locator. Registry values are untrusted file paths:
/// accept only an existing regular steam.exe on a fully-qualified local volume.
/// Never treat registry contents as command-line arguments or store credentials.
/// </summary>
public static class SteamInstallLocator
{
    internal static string? FindExecutable(IEnumerable<string> candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 1024)
                continue;
            try
            {
                string value = candidate.Trim().Trim('"');
                if (!Path.IsPathFullyQualified(value) ||
                    value.StartsWith(@"\\", StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileName(value), "steam.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                string path = Path.GetFullPath(value);
                if (GamingLocalFileSafetyPolicy.IsSafeRegularFile(
                    path, GamingLocalFileSafetyPolicy.ExecutableExtensions))
                    return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
            {
                // Ignore untrusted or unavailable candidate and try the next.
            }
        }
        return null;
    }

    public static string? FindExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var paths = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? steam = root.OpenSubKey(@"SOFTWARE\Valve\Steam", writable: false);
                if (steam is null) continue;
                if (steam.GetValue("SteamExe") is string exe)
                    paths.Add(exe);
                if (steam.GetValue("SteamPath") is string folder)
                    paths.Add(Path.Combine(folder, "steam.exe"));
                if (steam.GetValue("InstallPath") is string install)
                    paths.Add(Path.Combine(install, "steam.exe"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or SecurityException)
            {
                // Registry may be missing or inaccessible in a non-admin session.
            }
        }

        foreach (Environment.SpecialFolder special in new[]
        {
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.LocalApplicationData
        })
        {
            string folder = Environment.GetFolderPath(special);
            if (!string.IsNullOrWhiteSpace(folder))
                paths.Add(Path.Combine(folder, "Steam", "steam.exe"));
        }
        return FindExecutable(paths);
    }

    public static string? FindLibraryRoot()
    {
        string? executable = FindExecutable();
        return executable is null ? null : Path.GetDirectoryName(executable);
    }
}
