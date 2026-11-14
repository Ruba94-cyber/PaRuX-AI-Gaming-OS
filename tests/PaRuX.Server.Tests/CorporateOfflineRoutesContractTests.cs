using System;
using System.IO;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-package finding (2026-11-14): in Corporate Lite the runtime/engine installers (/api/v1/runtime/builds,
/// /api/v1/runtime/engines) were not covered by the edition guard (only the dead proxy stopped them, with a network
/// error instead of an honest refusal), and POST /api/v1/internet {enabled:true} answered "enabled": true while the
/// effective state stayed off. Source contracts pin the guard; the behaviour was re-verified on the running Core.
/// </summary>
public sealed class CorporateOfflineRoutesContractTests
{
    private static string Server()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(dir!, "src", "PaRuX.Server", "Program.cs"));
    }

    [Fact]
    public void Corporate_guard_covers_runtime_builds_engines_and_their_catalog_lookups()
    {
        string server = Server();
        Assert.Contains("path.StartsWith(\"/api/v1/runtime/builds\"", server, StringComparison.Ordinal);
        Assert.Contains("path.StartsWith(\"/api/v1/runtime/engines\"", server, StringComparison.Ordinal);
        Assert.Contains("restricted && (write || gaming || catalogLookup)", server, StringComparison.Ordinal);
    }

    [Fact]
    public void Corporate_refuses_to_switch_internet_on_and_reports_the_effective_state()
    {
        string server = Server();
        int post = server.IndexOf("app.MapPost(\"/api/v1/internet\"", StringComparison.Ordinal);
        Assert.True(post > 0);
        string body = server.Substring(post, 900);
        Assert.Contains("request.Enabled && PaRuX.Contracts.CorporateMode.IsActive", body, StringComparison.Ordinal);
        Assert.Contains("Enabled = internet.GetEnabled()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Enabled = request.Enabled", body, StringComparison.Ordinal);
    }
}
