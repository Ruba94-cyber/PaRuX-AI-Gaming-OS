namespace PaRuX.Contracts;

/// <summary>Roles the owner can give a paired device or a browser session. The desktop owner is always the owner.</summary>
public static class PrincipalRoles
{
    /// <summary>Everything a paired device or browser session could already do (the default; nothing gets more rights than before).</summary>
    public const string Operator = "operator";
    /// <summary>Read only: it can look at chats, jobs and the library, and change nothing and start nothing.</summary>
    public const string Viewer = "viewer";

    public static readonly string[] All = { Operator, Viewer };

    public static bool IsValid(string? role) => role is Operator or Viewer;
}

public sealed class PrincipalRoleEntry
{
    /// <summary>"device:&lt;id&gt;" or "web:&lt;id&gt;", the same identifiers the Core already uses for these principals.</summary>
    public string PrincipalId { get; set; } = string.Empty;
    public string Role { get; set; } = PrincipalRoles.Operator;
    public DateTime GrantedUtc { get; set; }
}

public sealed class SetPrincipalRoleRequest
{
    public string PrincipalId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
