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

    public static bool SupportsOfficialLaunch(string? providerId, string? id) =>
        providerId?.ToLowerInvariant() switch
        {
            "steam" => Steam(id),
            "epic" => Epic(id),
            "gog" => Gog(id),
            _ => false
        };
}
