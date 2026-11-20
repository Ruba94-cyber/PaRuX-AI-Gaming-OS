namespace PaRuX.Contracts;

/// <summary>Owner-supplied Steam Web API credentials. The key is never returned after it is saved.</summary>
public sealed class SteamCredentialsRequest
{
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>The 17-digit SteamID64 whose achievements to read (the owner's own profile must have game details public).</summary>
    public string SteamId { get; set; } = string.Empty;
}

public sealed class SteamCredentialsStatus
{
    public bool Configured { get; set; }
    public string SteamId { get; set; } = string.Empty;
}

public sealed class SteamAchievementDto
{
    public string ApiName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Achieved { get; set; }
    public DateTime? UnlockedUtc { get; set; }
}

public sealed class SteamAchievementsResult
{
    public int AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Unlocked { get; set; }
    public List<SteamAchievementDto> Achievements { get; set; } = new();
    /// <summary>Empty on success; otherwise a plain reason (no credentials, Internet off, private profile, no achievements).</summary>
    public string Problem { get; set; } = string.Empty;
}
