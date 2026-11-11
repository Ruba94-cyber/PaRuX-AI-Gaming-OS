using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming.Downloads;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Microsoft Store edition policy: no executable downloads and no torrent engine. "Packaged" is simulated with the
/// async-local test seam, so no real package is needed and other tests are unaffected.</summary>
public sealed class StoreEditionPolicyTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PureRules_AllowExecutableDownloadsAndTorrentOnlyOutsideTheStoreEdition(bool packaged, bool allowed)
    {
        Assert.Equal(allowed, PackageIdentity.AllowsExecutableDownloadsFor(packaged));
        Assert.Equal(allowed, PackageIdentity.AllowsTorrentFor(packaged));
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(false, "0", false)]
    [InlineData(false, "1", true)]
    [InlineData(false, " true ", true)]
    [InlineData(true, null, true)]
    [InlineData(true, "0", true)]
    public void StoreEdition_IsPackageIdentityOrTheExplicitEnvironmentSignal(bool packaged, string? env, bool expected)
        => Assert.Equal(expected, PackageIdentity.IsStoreEditionFor(packaged, env));

    [Fact]
    public void Unpackaged_BuildsAreUnchanged()
    {
        using IDisposable _ = StoreEditionPolicy.Simulate(false);
        Assert.True(StoreEditionPolicy.AllowsExecutableDownloads);
        Assert.True(StoreEditionPolicy.AllowsTorrent);
        StoreEditionPolicy.RequireExecutableDownloads();
        StoreEditionPolicy.RequireTorrent();
    }

    [Theory]
    [InlineData("POST", "/api/v1/runtime/builds/install")]
    [InlineData("POST", "/api/v1/runtime/builds/install/abc/resume")]
    [InlineData("GET", "/api/v1/runtime/builds/available")]
    [InlineData("POST", "/api/v1/runtime/engines/exllamav3/install")]
    [InlineData("POST", "/api/v1/maintenance/runtime-update/run-now")]
    [InlineData("POST", "/api/v1/embeddings/engine/install")]
    [InlineData("POST", "/api/v1/reranker/engine/install")]
    [InlineData("POST", "/api/v1/avatar/engine/install")]
    [InlineData("POST", "/api/v1/training/engine/install")]
    [InlineData("POST", "/api/v2/training/engine/install")]
    [InlineData("POST", "/api/v1/adapters/conversion/install")]
    [InlineData("POST", "/api/v1/speech/engine/install")]
    [InlineData("POST", "/api/v1/threed/engine/install")]
    [InlineData("POST", "/api/v1/cutout/engine/install")]
    [InlineData("POST", "/api/v2/media/engines/ref2va/install")]
    [InlineData("POST", "/api/v2/media/engines/diffusers/install")]
    [InlineData("POST", "/api/v1/media/setup/start")]
    [InlineData("POST", "/api/v1/gaming/downloads")]
    [InlineData("POST", "/API/V1/Gaming/Downloads/")]
    [InlineData("POST", "/api/v1/gaming/downloads/abc/resume")]
    [InlineData("POST", "/api/v1/gaming/downloads/abc/portable/retry")]
    [InlineData("PUT", "/api/v1/gaming/downloads/torrent-client")]
    public void StoreEdition_RefusesEveryExecutableDownloadAndTorrentRoute(string method, string path)
    {
        string? reason = StoreEditionPolicy.BlockedReason(true, method, path);
        Assert.NotNull(reason);
        Assert.StartsWith("Not available in the Microsoft Store edition", reason);
        Assert.Null(StoreEditionPolicy.BlockedReason(false, method, path)); // MSI/Portable/Corporate unchanged
    }

    [Theory]
    [InlineData("GET", "/api/v1/runtime/components")]
    [InlineData("POST", "/api/v1/runtime/components/llama-server-cpu/locate")]
    [InlineData("POST", "/api/v1/runtime/components/comfyui/skip")]
    [InlineData("POST", "/api/v1/models/downloads")]
    [InlineData("POST", "/api/v1/models/download-plan/start")]
    [InlineData("POST", "/api/v1/media/companions/fetch")]
    [InlineData("POST", "/api/v1/media/loras/install")]
    [InlineData("POST", "/api/v1/updates/models/install")]
    [InlineData("POST", "/api/v1/datasets/downloads")]
    [InlineData("GET", "/api/v1/gaming/downloads")]
    [InlineData("GET", "/api/v1/gaming/downloads/torrent-engine")]
    [InlineData("DELETE", "/api/v1/gaming/downloads/abc")]
    [InlineData("POST", "/api/v1/gaming/emulators/import")]
    [InlineData("POST", "/api/v1/gaming/installers/inspect")]
    [InlineData("POST", "/api/v2/plugins/install")]
    [InlineData("POST", "/api/v1/chat/completions")]
    public void StoreEdition_KeepsModelDownloadsLocateExistingLibraryAndChatWorking(string method, string path)
        => Assert.Null(StoreEditionPolicy.BlockedReason(true, method, path));

    [Fact]
    public async Task Guard_RefusesWith403AndReadableReasonBeforeTheEndpointRuns()
    {
        bool reached = false;
        var guard = new StoreEditionGuardMiddleware(_ => { reached = true; return Task.CompletedTask; }, () => true);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/runtime/builds/install";
        context.Response.Body = new MemoryStream();

        await guard.InvokeAsync(context);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("Not available in the Microsoft Store edition", body);
    }

    [Theory]
    [InlineData(true, "/api/v1/models/downloads")]
    [InlineData(false, "/api/v1/runtime/builds/install")]
    [InlineData(false, "/api/v1/gaming/downloads")]
    public async Task Guard_IsAPassThroughForAllowedRequestsAndOutsideTheStoreEdition(bool store, string path)
    {
        bool reached = false;
        var guard = new StoreEditionGuardMiddleware(_ => { reached = true; return Task.CompletedTask; }, () => store);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;

        await guard.InvokeAsync(context);

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("No network request may be made in the Store edition.");
        }
    }

    [Theory]
    [InlineData(GameDownloadKinds.Http, "https://example.invalid/game.zip")]
    [InlineData(GameDownloadKinds.Magnet, "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    public void GameDownloadManager_RefusesEveryDownloadKindWithoutTouchingNetworkOrDisk(string kind, string url)
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-store-dl-" + Guid.NewGuid().ToString("N"));
        var handler = new NoNetworkHandler();
        using IDisposable _ = StoreEditionPolicy.Simulate(true);
        using var manager = new GameDownloadManager(new HttpClient(handler), () => false, _ => long.MaxValue, null, Array.Empty<TimeSpan>());

        var ex = Assert.Throws<GameDownloadException>(() => manager.Enqueue(new GameDownloadRequest(url, dir, Kind: kind)));

        Assert.StartsWith("Not available in the Microsoft Store edition", ex.Message);
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task EmbeddedTorrentTransport_NeverStartsTheEngineInTheStoreEdition()
    {
        string cache = Path.Combine(Path.GetTempPath(), "parux-store-torrent-" + Guid.NewGuid().ToString("N"));
        using IDisposable _ = StoreEditionPolicy.Simulate(true);
        using var transport = new EmbeddedTorrentTransport(() => false, cache);
        var context = new GameDownloadTransferContext
        {
            Job = new GameDownloadJob { Id = "x", Kind = GameDownloadKinds.Magnet, Directory = Path.GetTempPath(), FileName = "a.bin" },
            GamingActive = () => false,
            FreeBytes = _ => long.MaxValue,
            MaxFileBytes = 16,
            Throttle = (_, _) => Task.CompletedTask,
            Report = (_, _) => { },
            UserCancelled = () => false
        };

        await Assert.ThrowsAsync<StoreEditionRestrictedException>(
            () => ((IGameDownloadTransport)transport).FetchAsync(context, CancellationToken.None));

        Assert.False(Directory.Exists(cache)); // no engine, no cache folder, no listener
    }

    [Fact]
    public async Task ManagedPythonTooling_RefusesPackageInstallsInTheStoreEdition()
    {
        using IDisposable _ = StoreEditionPolicy.Simulate(true);
        await Assert.ThrowsAsync<StoreEditionRestrictedException>(
            () => ManagedPythonRuntime.PipInstallAsync(Path.Combine(Path.GetTempPath(), "no-venv"), new[] { "x" }, CancellationToken.None));
        await Assert.ThrowsAsync<StoreEditionRestrictedException>(
            () => ManagedSourceArchive.EnsureGithubCommitAsync("owner", "repo", new string('a', 40),
                Path.Combine(Path.GetTempPath(), "parux-store-src-" + Guid.NewGuid().ToString("N")), CancellationToken.None));
    }

    // ---- source contracts: the wiring that makes the Core authoritative and the UI honest ----

    [Fact]
    public void Core_RegistersTheGuardAndThePackagedAppPassesTheEditionExplicitly()
    {
        Assert.Contains("app.UseMiddleware<StoreEditionGuardMiddleware>();", File.ReadAllText(Source("src", "PaRuX.Server", "Program.cs")));
        string controller = File.ReadAllText(Source("src", "PaRuX.App", "Services", "PaRuXServerController.cs"));
        Assert.Contains("PackageIdentity.IsPackaged", controller);
        Assert.Contains("StoreEditionEnvironmentVariable", controller);
    }

    [Fact]
    public void Ui_HidesOrDisablesExecutableDownloadAndTorrentControlsInTheStoreEdition()
    {
        Assert.Contains("StoreEditionPolicy.AllowsExecutableDownloads", File.ReadAllText(Source("src", "PaRuX.App", "Views", "GameDownloadsPanel.cs")));
        Assert.Contains("ApplyStoreEditionRestrictions();", File.ReadAllText(Source("src", "PaRuX.App", "Views", "SettingsView.xaml.cs")));
        Assert.Contains("StoreEditionPolicy.AllowsExecutableDownloads", File.ReadAllText(Source("src", "PaRuX.App", "Views", "ModelHubView.Tabs.cs")));
        Assert.Contains("StoreEditionPolicy.AllowsExecutableDownloads", File.ReadAllText(Source("src", "PaRuX.App", "Views", "OnboardingView.AutoSetup.cs")));
        // Components keep "locate"/"relocate" and only lose "install".
        string inventory = File.ReadAllText(Source("src", "PaRuX.Server", "ComponentInventoryService.cs"));
        Assert.Contains("StoreEditionPolicy.AllowsExecutableDownloads", inventory);
        Assert.Contains("\"locate\"", inventory);
    }

    private static string Source(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaRuX.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("PaRuX.sln could not be located.");
        return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
    }
}
