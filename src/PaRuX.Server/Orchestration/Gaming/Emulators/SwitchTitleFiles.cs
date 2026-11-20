using System.Text.RegularExpressions;

namespace PaRuX.Server.Orchestration.Gaming.Emulators;

/// <summary>
/// Nintendo Switch dumps arrive as separate files for the game, its updates and its add-on content. Only the game is a library
/// entry: listing "Mario Kart 8 Deluxe [v1179648][UPD]" next to "Mario Kart 8 Deluxe" doubled the library and made
/// updates look like games. The 16-hex-digit title id in the file name tells them apart: a base title ends in 000, an update in
/// 800, anything else is add-on content. Explicit [UPD], [DLC] or [Update] tags in the name win. Names are only read, never opened.
/// </summary>
public static class SwitchTitleFiles
{
    private static readonly Regex TitleId = new(@"\[(?<id>[0-9A-Fa-f]{16})\]", RegexOptions.Compiled);
    private static readonly Regex AddOnTag = new(@"\[(?:UPD|UPDATE|DLC)[^\]]*\]|\bupdate v?\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".nsp", ".xci", ".nsz", ".xcz" };

    /// <summary>True when the file is Switch update or add-on content that should not be a library entry of its own.</summary>
    public static bool IsAddOn(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !Extensions.Contains(Path.GetExtension(fileName))) return false;
        if (AddOnTag.IsMatch(fileName)) return true;
        Match m = TitleId.Match(fileName);
        if (!m.Success) return false;
        return !m.Groups["id"].Value.EndsWith("000", StringComparison.Ordinal);
    }
}
