namespace PaRuX.Contracts;

/// <summary>
/// Pure first-party admission rules for IDs passed to official store clients.
/// The URI/executable path is ALWAYS selected again from trusted local
/// discovery; a validated identifier alone grants no execution authority.
/// </summary>
public static class GameStoreLaunchIdPolicy
{
    public static bool Steam(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 15 && id.All(char.IsAsciiDigit);

    public static bool Gog(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 24 && id.All(char.IsAsciiDigit);

    public static bool Epic(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 200 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>EA app / Origin offer id (the Core re-validates it against the game's own install data).</summary>
    public static bool Ea(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 40 &&
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^(Origin\.OFR\.\d{1,3}\.\d{1,10}|OFB-[A-Z]{2,8}:\d{1,12})$");

    /// <summary>Battle.net product code (for example a short lower-case game code).</summary>
    public static bool BattleNet(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length is >= 2 and <= 24 &&
        id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    public static bool Ubisoft(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 12 && id.All(char.IsAsciiDigit);

    /// <summary>Xbox / Microsoft Store activation id: PackageFamilyName!ApplicationId.</summary>
    public static bool Xbox(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 160 &&
        System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9.\-]{3,64}_[a-z0-9]{13}![A-Za-z0-9.\-_]{1,64}$");

    public static bool SupportsOfficialLaunch(string? providerId, string? id) =>
        providerId?.ToLowerInvariant() switch
        {
            "steam" => Steam(id),
            "epic" => Epic(id),
            "gog" => Gog(id),
            "ea" => Ea(id),
            "battlenet" => BattleNet(id),
            "ubisoft" => Ubisoft(id),
            "xbox" => Xbox(id),
            _ => false
        };
}
