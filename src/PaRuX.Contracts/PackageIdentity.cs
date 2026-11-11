using System.Runtime.InteropServices;

namespace PaRuX.Contracts;

/// <summary>
/// Runtime detection of MSIX package identity (the Microsoft Store edition) and the pure rules that
/// depend on it. Detection is a runtime question - the same binaries run unpackaged (MSI/Portable)
/// and packaged (Store) - so nothing here is a compile-time constant.
///
/// A packaged full-trust process has its writes under %LOCALAPPDATA% silently redirected to a
/// per-package private location while existing real files are still read and modified in place.
/// Desktop, Core and every child process must agree on ONE location, so a packaged process uses the
/// real, unvirtualized package state folder explicitly (<c>%LOCALAPPDATA%\Packages\PFN\LocalState</c>).
/// </summary>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const string DataFolderName = "PaRuX";

    private static readonly Lazy<string?> Family = new(QueryFamilyName);

    /// <summary>Package family name of the current process, or null when it is unpackaged.</summary>
    public static string? FamilyName => Family.Value;

    /// <summary>True when the current process runs with package identity (the Store edition).</summary>
    public static bool IsPackaged => !string.IsNullOrEmpty(FamilyName);

    /// <summary>
    /// In-app replacement of the application itself (the signed-MSI updater) must not run for a
    /// packaged install: the Microsoft Store owns updates and a parallel MSI would create a second,
    /// conflicting installation. Corporate offline mode keeps its own separate gate.
    /// </summary>
    public static bool AllowsInAppSelfUpdate => AllowsInAppSelfUpdateFor(IsPackaged);

    internal static bool AllowsInAppSelfUpdateFor(bool isPackaged) => !isPackaged;

    /// <summary>Unvirtualized per-package data folder: <c>{localAppData}\Packages\{pfn}\LocalState</c>.</summary>
    internal static string PackagedStateRoot(string localAppData, string packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(localAppData)) throw new ArgumentException("LocalAppData is required.", nameof(localAppData));
        if (!IsValidFamilyName(packageFamilyName)) throw new ArgumentException("Not a valid package family name.", nameof(packageFamilyName));
        return System.IO.Path.Combine(localAppData, "Packages", packageFamilyName, "LocalState");
    }

    /// <summary>The PaRuX data root of a packaged process.</summary>
    internal static string PackagedDataRoot(string localAppData, string packageFamilyName)
        => System.IO.Path.Combine(PackagedStateRoot(localAppData, packageFamilyName), DataFolderName);

    /// <summary>The LocalAI coordination-state folder of a packaged process.</summary>
    internal static string PackagedLocalAiStateRoot(string localAppData, string packageFamilyName)
        => System.IO.Path.Combine(PackagedStateRoot(localAppData, packageFamilyName), "LocalAI");

    /// <summary>Family names are "Name_publisherId": letters, digits, dot, dash, one underscore. This
    /// also keeps a hostile value from escaping the Packages folder.</summary>
    internal static bool IsValidFamilyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        int underscore = value.IndexOf('_');
        if (underscore <= 0 || underscore != value.LastIndexOf('_') || underscore == value.Length - 1) return false;
        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return false;
        }
        return !value.Contains("..", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, [Out] char[]? name);

    private static string? QueryFamilyName()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            int length = 0;
            int result = GetCurrentPackageFamilyName(ref length, null);
            if (result == AppModelErrorNoPackage) return null;
            if (result != ErrorInsufficientBuffer || length <= 1) return null;
            var buffer = new char[length];
            result = GetCurrentPackageFamilyName(ref length, buffer);
            if (result != 0) return null;
            string name = new string(buffer, 0, length - 1);
            return IsValidFamilyName(name) ? name : null;
        }
        catch (Exception) { return null; } // missing export on very old systems: treat as unpackaged
    }
}
