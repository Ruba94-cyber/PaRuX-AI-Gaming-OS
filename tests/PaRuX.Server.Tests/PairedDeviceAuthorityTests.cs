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

/// <summary>SEC-F04: a paired device cannot enable the Internet switch or choose a task authority.</summary>
public sealed class PairedDeviceAuthorityTests : IAsyncLifetime
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
        _app.MapPost("/internet", (InternetEnabledRequest request) => Results.Ok(new { request.Enabled }))
            .RequireDesktopOwnerToEnableInternet();
        _app.MapPost("/tasks", (CreateAgentTaskRequest request) => Results.Ok(new
        {
            mode = request.CodingRunPermissionMode,
            caps = request.GrantedCapabilities
        })).ConstrainPairedDeviceTaskAuthority();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private Task<HttpResponseMessage> Post(string path, string owner, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Owner", owner);
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData("device:phone")]
    [InlineData("web:session")]
    public async Task Non_desktop_principal_cannot_turn_the_internet_on_but_may_turn_it_off(string owner)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/internet", owner, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post("/internet", owner, new { enabled = false })).StatusCode);
    }

    [Fact]
    public async Task Desktop_owner_can_turn_the_internet_on()
    {
        Assert.Equal(HttpStatusCode.OK, (await Post("/internet", "desktop", new { enabled = true })).StatusCode);
    }

    [Theory]
    [InlineData("mcp.external")]
    [InlineData("coding.package-restore")]
    [InlineData("coding.package-restore.scripts")]
    [InlineData("agent.delegate")]
    [InlineData("something.unknown")]
    public async Task Paired_device_cannot_grant_privileged_or_unknown_capabilities(string capability)
    {
        HttpResponseMessage response = await Post("/tasks", "device:phone", new
        {
            goal = "x",
            grantedCapabilities = new[] { "context.read", capability }
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Paired_device_task_keeps_allowed_capabilities_and_is_forced_to_ask_mode()
    {
        HttpResponseMessage response = await Post("/tasks", "device:phone", new
        {
            goal = "x",
            codingRunPermissionMode = "bypass",
            grantedCapabilities = new[] { "context.read", "Web.Research", "repository.read" }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ask", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("caps").GetArrayLength());
    }

    [Fact]
    public async Task Paired_device_delegated_child_does_not_carry_a_requested_mode()
    {
        HttpResponseMessage response = await Post("/tasks", "device:phone", new
        {
            goal = "x", parentId = "p1", codingRunPermissionMode = "bypass"
        });
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("mode").ValueKind);
    }

    [Fact]
    public async Task Desktop_owner_keeps_full_control_of_capabilities_and_mode()
    {
        HttpResponseMessage response = await Post("/tasks", "desktop", new
        {
            goal = "x", codingRunPermissionMode = "bypass", grantedCapabilities = new[] { "mcp.external" }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("bypass", json.RootElement.GetProperty("mode").GetString());
    }
}

/// <summary>Proves the real routes carry the SEC-F04 filters, not only the helper extensions.</summary>
public sealed class PairedDeviceAuthorityWiringTests : IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private async Task<int> InvokeAsPairedDevice(string path, string json)
    {
        RouteEndpoint endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == path
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
    public async Task Real_internet_route_refuses_a_paired_device_enabling_it()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await InvokeAsPairedDevice("/api/v1/internet", "{\"enabled\":true}"));
    }

    [Fact]
    public async Task Real_task_route_refuses_a_paired_device_granting_mcp_external()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await InvokeAsPairedDevice(
            "/api/v1/orchestrator/tasks", "{\"goal\":\"x\",\"grantedCapabilities\":[\"mcp.external\"]}"));
    }
}
