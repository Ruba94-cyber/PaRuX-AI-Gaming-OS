using System.Text;

namespace PaRuX.Server.Orchestration.Gaming.Emulators;

public sealed record EmulatorLaunchPlan(string Arguments, IReadOnlyList<string> ArgumentList, string? Error);

/// <summary>
/// Turns a definition plus the user's choices into a Windows command-line string for <c>ProcessStartInfo.Arguments</c>
/// (the process is started without a shell). Every value is quoted per the Windows argument rules, control characters
/// are refused, and the user's extra arguments are inserted verbatim only where the template allows it.
/// </summary>
public static class EmulatorCommandBuilder
{
    public const int MaxExtraArgumentsLength = 512;

    public static EmulatorLaunchPlan Build(
        EmulatorDefinition definition, string romPath, bool fullscreen, string? extraArguments, string? corePath)
    {
        if (string.IsNullOrWhiteSpace(romPath) || romPath.Length > 1024 || romPath.Any(char.IsControl) || !Path.IsPathFullyQualified(romPath))
            return Fail("The game path must be an absolute local path.");
        string extra = (extraArguments ?? string.Empty).Trim();
        if (extra.Length > MaxExtraArgumentsLength || extra.Any(char.IsControl))
            return Fail("Extra arguments are too long or contain control characters.");
        if (definition.RequiresCore && (string.IsNullOrWhiteSpace(corePath) || !Path.IsPathFullyQualified(corePath) || corePath.Any(char.IsControl)))
            return Fail("This emulator needs a core file (absolute path).");

        string full = Path.GetFullPath(romPath);
        var arguments = new List<string>();      // already-quoted fragments, in order
        var plain = new List<string>();          // the same list without the user's free-form text, for display and tests
        foreach (string token in definition.Template)
        {
            switch (token)
            {
                case "{rom}": Add(full); break;
                case "{romdir}": Add(Path.GetDirectoryName(full) ?? string.Empty); break;
                case "{romname}": Add(Path.GetFileNameWithoutExtension(full)); break;
                case "{core}": Add(Path.GetFullPath(corePath!)); break;
                case "{fullscreen}":
                    if (fullscreen && definition.FullscreenArguments is { Count: > 0 } flags)
                        foreach (string flag in flags) { arguments.Add(Quote(flag)); plain.Add(flag); }
                    break;
                case "{extra}":
                    if (extra.Length > 0) { arguments.Add(extra); plain.Add(extra); }
                    break;
                default:
                    arguments.Add(Quote(token)); plain.Add(token);
                    break;
            }
        }
        return new EmulatorLaunchPlan(string.Join(' ', arguments), plain, null);

        void Add(string value) { arguments.Add(Quote(value)); plain.Add(value); }
    }

    /// <summary>Quotes one argument per the rules of CommandLineToArgvW (backslashes before a quote are doubled).</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
            else { sb.Append('\\', backslashes).Append(c); }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static EmulatorLaunchPlan Fail(string error) => new(string.Empty, Array.Empty<string>(), error);
}
