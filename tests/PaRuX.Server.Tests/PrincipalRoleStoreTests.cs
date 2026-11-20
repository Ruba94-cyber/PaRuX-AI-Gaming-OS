using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PrincipalRoleStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-roles-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "principal-roles.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Everyone_is_an_operator_until_the_owner_says_otherwise()
    {
        var store = new PrincipalRoleStore(FilePath);
        Assert.Equal(PrincipalRoles.Operator, store.RoleOf("device:abc"));
        Assert.Equal(PrincipalRoles.Operator, store.RoleOf("web:xyz"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void A_viewer_role_survives_a_reopen_and_returning_to_operator_forgets_the_entry()
    {
        Assert.Null(new PrincipalRoleStore(FilePath).Set("device:abc", PrincipalRoles.Viewer));
        var reopened = new PrincipalRoleStore(FilePath);
        Assert.Equal(PrincipalRoles.Viewer, reopened.RoleOf("device:abc"));
        Assert.Equal(PrincipalRoles.Operator, reopened.RoleOf("device:other"));

        Assert.Null(reopened.Set("device:abc", PrincipalRoles.Operator));
        Assert.Empty(reopened.List());
        Assert.Equal(PrincipalRoles.Operator, new PrincipalRoleStore(FilePath).RoleOf("device:abc"));
    }

    [Theory]
    [InlineData("desktop", "viewer")]
    [InlineData("apikey:k1", "viewer")]
    [InlineData("device:", "viewer")]
    [InlineData("device:abc", "owner")]
    [InlineData("device:abc", "")]
    [InlineData("", "viewer")]
    public void Only_devices_and_browser_sessions_get_roles_and_only_known_ones(string principal, string role)
    {
        var store = new PrincipalRoleStore(FilePath);
        Assert.NotNull(store.Set(principal, role));
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData("operator", "POST", true)]
    [InlineData("operator", "GET", true)]
    [InlineData("viewer", "GET", true)]
    [InlineData("viewer", "HEAD", true)]
    [InlineData("viewer", "OPTIONS", true)]
    [InlineData("viewer", "POST", false)]
    [InlineData("viewer", "PUT", false)]
    [InlineData("viewer", "DELETE", false)]
    [InlineData("viewer", "PATCH", false)]
    [InlineData("mystery", "GET", false)]
    public void The_gate_lets_a_viewer_read_and_nothing_else(string role, string method, bool allowed) =>
        Assert.Equal(allowed, PrincipalRoleStore.Allows(role, method, "/api/v1/anything"));

    private static async Task<(int Status, bool Reached)> Run(PrincipalRoleStore store, string? owner, string method, string path)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (owner is not null) context.Items["PaRuXOwner"] = owner;
        bool reached = false;
        await PrincipalRoleStore.EnforceAsync(context, store, _ => { reached = true; return Task.CompletedTask; });
        return (context.Response.StatusCode, reached);
    }

    [Fact]
    public async Task The_request_gate_refuses_a_read_only_device_a_write_and_lets_everyone_else_through()
    {
        var store = new PrincipalRoleStore(FilePath);
        store.Set("device:phone", PrincipalRoles.Viewer);
        store.Set("web:laptop", PrincipalRoles.Viewer);

        Assert.Equal((403, false), await Run(store, "device:phone", "POST", "/api/v1/chat/completions"));
        Assert.Equal((403, false), await Run(store, "web:laptop", "DELETE", "/api/v1/conversations/x"));
        Assert.Equal((200, true), await Run(store, "device:phone", "GET", "/api/v1/health"));

        // A device that was never narrowed, the desktop owner and an API key are untouched by roles.
        Assert.Equal((200, true), await Run(store, "device:other", "POST", "/api/v1/chat/completions"));
        Assert.Equal((200, true), await Run(store, "desktop", "POST", "/api/v1/settings"));
        Assert.Equal((200, true), await Run(store, "apikey:k1", "POST", "/v1/chat/completions"));
        Assert.Equal((200, true), await Run(store, null, "POST", "/api/v1/remote/pair/complete"));
    }

    [Fact]
    public async Task Returning_a_device_to_full_access_takes_effect_on_its_next_request()
    {
        var store = new PrincipalRoleStore(FilePath);
        store.Set("device:phone", PrincipalRoles.Viewer);
        Assert.False((await Run(store, "device:phone", "POST", "/api/v1/x")).Reached);
        store.Set("device:phone", PrincipalRoles.Operator);
        Assert.True((await Run(store, "device:phone", "POST", "/api/v1/x")).Reached);
    }

    [Fact]
    public void A_damaged_file_means_everyone_keeps_the_default_never_more_rights()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var store = new PrincipalRoleStore(FilePath);
        Assert.Equal(PrincipalRoles.Operator, store.RoleOf("device:abc"));
    }

    [Fact]
    public void Entries_with_an_unknown_role_in_the_file_are_ignored_on_load()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "[{\"principalId\":\"device:a\",\"role\":\"superuser\"},{\"principalId\":\"device:b\",\"role\":\"viewer\"}]");
        var store = new PrincipalRoleStore(FilePath);
        Assert.Equal(PrincipalRoles.Operator, store.RoleOf("device:a"));
        Assert.Equal(PrincipalRoles.Viewer, store.RoleOf("device:b"));
    }
}
