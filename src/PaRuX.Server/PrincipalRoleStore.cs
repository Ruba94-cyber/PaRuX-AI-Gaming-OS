using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Who may do what, for the people and devices the owner has let in. The desktop owner is always allowed everything; this store only
/// narrows a paired device or a browser session. A missing or damaged file means the default role, which is exactly what those
/// principals could do before roles existed, so reading can never grant more than before. Every change is recorded in the audit log.
/// </summary>
public sealed class PrincipalRoleStore
{
    internal const int MaximumEntries = 500;
    private const long MaxFileBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<PrincipalRoleEntry> _items = new();

    public PrincipalRoleStore() : this(Path.Combine(ServerState.StateDirectory, "principal-roles.json")) { }

    public PrincipalRoleStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= MaxFileBytes)
                _items = (JsonSerializer.Deserialize<List<PrincipalRoleEntry>>(File.ReadAllText(_path), JsonOptions) ?? new())
                    .Where(e => ValidPrincipal(e.PrincipalId) && PrincipalRoles.IsValid(e.Role)).Take(MaximumEntries).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>Only paired devices and browser sessions carry a role; the desktop owner and API keys do not.</summary>
    public static bool ValidPrincipal(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 120 && !id.Any(char.IsControl)
        && (id.StartsWith("device:", StringComparison.Ordinal) || id.StartsWith("web:", StringComparison.Ordinal))
        && id.IndexOf(':') < id.Length - 1;

    public string RoleOf(string principalId)
    {
        lock (_gate) return _items.FirstOrDefault(e => string.Equals(e.PrincipalId, principalId, StringComparison.Ordinal))?.Role ?? PrincipalRoles.Operator;
    }

    public IReadOnlyList<PrincipalRoleEntry> List()
    {
        lock (_gate) return _items.Select(e => new PrincipalRoleEntry { PrincipalId = e.PrincipalId, Role = e.Role, GrantedUtc = e.GrantedUtc }).ToList();
    }

    /// <returns>Null on success, otherwise the reason. Setting the default role removes the entry (nothing to remember).</returns>
    public string? Set(string principalId, string role)
    {
        if (!ValidPrincipal(principalId)) return "Roles apply to a paired device (device:...) or a browser session (web:...).";
        if (!PrincipalRoles.IsValid(role)) return "Role must be \"operator\" or \"viewer\".";
        lock (_gate)
        {
            var next = _items.Where(e => !string.Equals(e.PrincipalId, principalId, StringComparison.Ordinal)).ToList();
            if (role != PrincipalRoles.Operator)
            {
                if (next.Count >= MaximumEntries) return "At most " + MaximumEntries + " role entries.";
                next.Add(new PrincipalRoleEntry { PrincipalId = principalId, Role = role, GrantedUtc = DateTime.UtcNow });
            }
            if (!Persist(next)) return "The role could not be saved.";
        }
        AuditLogService.TryRecord("role.set", principalId, role);
        return null;
    }

    /// <summary>
    /// Whether a principal with this role may make this request. Operator keeps every route it already had (the existing per-route
    /// device and session allow-lists still apply after this). Viewer may only read: GET, HEAD and OPTIONS, never anything that
    /// changes state or starts work. Unknown roles are treated as the most restrictive.
    /// </summary>
    public static bool Allows(string role, string method, string path)
    {
        if (role == PrincipalRoles.Operator) return true;
        bool readOnly = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (role == PrincipalRoles.Viewer) return readOnly;
        return false;
    }

    /// <summary>The request gate: a paired device or browser session with a narrowed role is refused anything that role does not allow.</summary>
    public static async Task EnforceAsync(Microsoft.AspNetCore.Http.HttpContext context, PrincipalRoleStore store, Microsoft.AspNetCore.Http.RequestDelegate next)
    {
        if (context.Items["PaRuXOwner"] is string who && (who.StartsWith("device:", StringComparison.Ordinal) || who.StartsWith("web:", StringComparison.Ordinal)))
        {
            string role = store.RoleOf(who);
            if (!Allows(role, context.Request.Method, context.Request.Path))
            {
                context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden;
                await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(context.Response, "{\"error\":\"This device or browser has read-only access.\"}");
                return;
            }
        }
        await next(context);
    }

    private bool Persist(List<PrincipalRoleEntry> next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _items = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
