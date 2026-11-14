using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// A managed Python install needs to compile a package from source (for example <c>insightface==0.7.3</c>, which is
/// sdist-only) and no usable C++ toolchain is present. The failure is typed so the setup status can show an honest,
/// localized "C++ build tools required" state instead of an unreadable compiler log.
/// </summary>
internal sealed class ManagedBuildToolsRequiredException(string package, string detail)
    : InvalidOperationException(ManagedBuildToolsFailure.EnglishMessage(package, detail))
{
    internal string Package { get; } = package;
}

internal static partial class ManagedBuildToolsFailure
{
    /// <summary>Stable machine-readable code carried by engine status so each UI localizes the text itself.</summary>
    internal const string Code = "build_tools_required";

    // Messages emitted by setuptools/distutils/uv when no compiler can be found or started.
    [GeneratedRegex(@"Unable to find a compatible Visual Studio installation|Microsoft Visual C\+\+ 14\.0 or greater is required|Unable to find vcvarsall\.bat|error: command 'cl\.exe' failed|error: command '.*cl\.exe' failed: No such file", RegexOptions.IgnoreCase)]
    private static partial Regex MissingToolchainPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"Failed to build `?(?<pkg>[A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex FailedPackagePattern();

    /// <summary>True when the pip/uv log shows a source build that failed only because the C++ toolchain is missing.</summary>
    internal static bool IsMissingToolchain(string log, out string package)
    {
        package = string.Empty;
        if (string.IsNullOrEmpty(log)) return false;
        // uv hard-wraps the build backend output at about 60 columns, which can split a message across lines.
        log = WhitespacePattern().Replace(log, " ");
        if (!MissingToolchainPattern().IsMatch(log)) return false;
        Match m = FailedPackagePattern().Match(log);
        package = m.Success ? m.Groups["pkg"].Value : "a Python package";
        return true;
    }

    internal static string EnglishMessage(string package, string detail) =>
        "C++ build tools are required: " + package + " is only published as source and has to be compiled on this PC, "
        + "but no usable Microsoft C++ compiler was found. Install \"Visual Studio Build Tools\" with the "
        + "\"Desktop development with C++\" workload, then retry. PaRuX does not install it for you."
        + (string.IsNullOrWhiteSpace(detail) ? string.Empty : "\n" + detail);
}
