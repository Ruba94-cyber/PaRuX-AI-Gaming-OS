namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Conservative patcher for one already-existing INI/CFG setting.
/// Never adds lines/sections, runs scripts or interprets game file contents.
/// Any ambiguity fails closed so the owner can review a full-file preview.
/// </summary>
internal static class GameIniSettingPatch
{
    private static bool IsIdentifier(string? s, bool allowEmpty) =>
        s is not null && (allowEmpty || s.Length > 0) && s.Length <= 80
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    internal static string Apply(string original, string? section, string? key, string? value)
    {
        section ??= "";
        if (!IsIdentifier(section, allowEmpty: true) || !IsIdentifier(key, allowEmpty: false))
            throw new ArgumentException("The INI section/key must be a simple ASCII identifier.");
        if (value is null || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("A setting value must be a single printable line up to 256 characters.");
        if (original is null || original.Length > 64 * 1024)
            throw new ArgumentException("The INI config is too large.");

        string[] lines = original.Split('\n');
        bool inSection = section.Length == 0;
        int matchCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = string.Equals(trimmed[1..^1].Trim(), section,
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0 || !string.Equals(line[..equals].Trim().TrimStart('\uFEFF'),
                    key, StringComparison.OrdinalIgnoreCase)) continue;
            matchCount++;
            // Do not permit a second definition (possibly in a repeated section).
            if (matchCount == 1)
                lines[i] = line[..(equals + 1)] + value + (line.EndsWith('\r') ? "\r" : "");
        }
        if (matchCount != 1)
            throw new ArgumentException("The requested INI setting is missing or duplicated.");
        return string.Join("\n", lines);
    }
}
