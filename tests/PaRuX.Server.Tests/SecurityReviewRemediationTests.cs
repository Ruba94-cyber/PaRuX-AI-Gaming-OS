using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F14: a skill that lists a tool PaRuX does not know is default-deny.</summary>
public sealed class SkillUnknownToolPolicyTests
{
    private static SkillManifest Manifest(SkillPermissions? permissions, params string[] tools) => new()
    {
        DisplayName = "Policy test", AllowedTools = tools.ToList(),
        Permissions = permissions ?? new SkillPermissions()
    };

    private static SkillPermissions All() => new()
    {
        ReadFiles = true, WriteFiles = true, DeleteFiles = true, Shell = true, Network = true,
        Browser = true, ComputerUse = true, ExternalProcesses = true
    };

    [Theory]
    [InlineData("bash")]
    [InlineData("powershell")]
    [InlineData("cmd")]
    [InlineData("pwsh")]
    [InlineData("python")]
    [InlineData("mcp__server__tool")]
    [InlineData("totally_unknown_tool")]
    public void An_unknown_tool_alias_is_rejected_without_every_permission(string tool)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SkillPermissionPolicy.Validate(Manifest(null, tool)));
        Assert.Contains("not a recognised PaRuX tool", error.Message);
    }

    [Fact]
    public void An_unknown_tool_with_only_some_permissions_is_still_rejected()
    {
        SkillPermissions partial = All();
        partial.ExternalProcesses = false;
        Assert.Throws<InvalidDataException>(() => SkillPermissionPolicy.Validate(Manifest(partial, "bash")));
    }

    [Fact]
    public void An_unknown_tool_passes_only_when_the_owner_granted_every_permission()
    {
        SkillPermissionPolicy.Validate(Manifest(All(), "bash"));
    }

    [Theory]
    [InlineData("git")]
    [InlineData("memory")]
    [InlineData("parux_desktop_search")]
    [InlineData("parux_desktop_read")]
    [InlineData("parux_kb_search")]
    [InlineData("parux_write_word_document")]
    [InlineData("parux_write_excel_workbook")]
    [InlineData("parux_edit_excel_workbook")]
    public void Known_no_permission_tools_still_validate_without_permissions(string tool)
    {
        SkillPermissionPolicy.Validate(Manifest(null, tool));
    }

    [Fact]
    public void Built_in_style_tool_lists_still_validate()
    {
        SkillPermissionPolicy.Validate(Manifest(null, "parux_desktop_search", "parux_desktop_read",
            "parux_write_word_document", "parux_write_excel_workbook", "parux_edit_excel_workbook"));
        SkillPermissions web = new() { Network = true, Browser = true };
        SkillPermissionPolicy.Validate(Manifest(web, "web_search", "web_fetch", "browser_*"));
    }

    [Fact]
    public void A_privileged_name_is_still_classified_by_its_permission_not_by_the_unknown_rule()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SkillPermissionPolicy.Validate(Manifest(null, "run_command")));
        Assert.Contains(nameof(SkillPermissions.Shell), error.Message);
    }
}

/// <summary>SEC-F04 residual: Autopilot started from a paired device is held for the desktop owner.</summary>
public sealed class PairedDeviceAutopilotAuthorityTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Items["PaRuXOwner"] = context.Request.Headers["X-Owner"].ToString();
            await next();
        });
        _app.MapPost("/runs", (StartAutopilotRunRequest request) => Results.Ok(new { mode = request.PermissionMode }))
            .ForceSupervisedAutopilotForPairedDevice();
        _app.MapPost("/runs/{id}/approve", (string id) => Results.NoContent()).RequireDesktopOwner();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> Post(string path, string owner, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("X-Owner", owner);
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("autonomous")]
    [InlineData("balanced")]
    [InlineData("supervised")]
    public async Task A_paired_device_run_is_always_started_supervised(string requested)
    {
        HttpResponseMessage response = await Post("/runs", "device:phone", new { goal = "x", permissionMode = requested });
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("supervised", json.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task A_browser_session_run_is_also_forced_to_supervised()
    {
        HttpResponseMessage response = await Post("/runs", "web:session", new { goal = "x", permissionMode = "autonomous" });
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("supervised", json.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task The_desktop_owner_keeps_choosing_the_autopilot_mode()
    {
        HttpResponseMessage response = await Post("/runs", "desktop", new { goal = "x", permissionMode = "autonomous" });
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("autonomous", json.RootElement.GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("device:phone", HttpStatusCode.Forbidden)]
    [InlineData("web:session", HttpStatusCode.Forbidden)]
    [InlineData("desktop", HttpStatusCode.NoContent)]
    public async Task Approving_a_run_needs_the_desktop_owner(string owner, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await Post("/runs/abc/approve", owner)).StatusCode);
    }

    [Fact]
    public void The_paired_device_allowlist_no_longer_contains_the_approve_route()
    {
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", "/api/v1/autopilot/runs/abc/approve"));
        Assert.True(PairedDeviceEndpointPolicy.IsAllowed("POST", "/api/v1/autopilot/runs"));
    }
}

/// <summary>Proves the real Autopilot routes carry the SEC-F04 gates.</summary>
public sealed class PairedDeviceAutopilotWiringTests : IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private async Task<int> InvokeAsPairedDevice(string pattern, string json)
    {
        RouteEndpoint endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == pattern
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        var context = new DefaultHttpContext { RequestServices = _factory.Services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Items["PaRuXOwner"] = "device:paired-id";
        context.Request.ContentType = "application/json";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = payload.Length;
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Real_approve_route_refuses_a_paired_device()
    {
        Assert.Equal(StatusCodes.Status403Forbidden,
            await InvokeAsPairedDevice("/api/v1/autopilot/runs/{id}/approve", "{}"));
    }
}
