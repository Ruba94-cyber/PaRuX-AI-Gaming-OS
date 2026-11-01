using System.Security;
using Microsoft.Win32;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Read-only local GOG GALAXY client discovery. Never accepts caller-provided
/// commands or launches registry data as shell text.
/// </summary>
public static class GogGalaxyLocator
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
                    !string.Equals(Path.GetFileName(value), "GalaxyClient.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                string path = Path.GetFullPath(value);
                // Reuse the local executable admission policy: the client must
                // not reside on a mapped network drive or beneath a junction.
                if (Providers.CustomExecutableInputProvider.IsSafeHelperPath(path))
                    return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or SecurityException)
            {
                // One corrupt registry value or missing location is not fatal.
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
                using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\GOG.com\GalaxyClient\paths", writable: false);
                if (key?.GetValue("client") is string value)
                {
                    value = value.Trim().Trim('"');
                    paths.Add(string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase)
                        ? value : Path.Combine(value, "GalaxyClient.exe"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or SecurityException)
            {
                // Read-only discovery must never edit the registry.
            }
        }

        foreach (Environment.SpecialFolder special in new[]
        {
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.ProgramFiles
        })
        {
            string folder = Environment.GetFolderPath(special);
            if (!string.IsNullOrWhiteSpace(folder))
                paths.Add(Path.Combine(folder, "GOG Galaxy", "GalaxyClient.exe"));
        }
        return FindExecutable(paths);
    }
}
