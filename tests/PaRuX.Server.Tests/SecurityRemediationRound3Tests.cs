using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Autopilot;
using PaRuX.Server.ContextGraph;
using PaRuX.Server.Orchestration;
using PaRuX.Server.ProjectManager;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Item 2: runs held for desktop approval can be listed, cancelled and expire.</summary>
public sealed class AutopilotPendingRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-autopilot-pending-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (AutopilotService svc, TaskStore tasks, FakeLocalCompletionClient client) Build()
    {
        var client = new FakeLocalCompletionClient();
        var tasks = new TaskStore(_dir);
        var pm = new ProjectManagerService(client, tasks, new ContextGraphStore(_dir));
        return (new AutopilotService(new AutopilotStore(_dir), pm, tasks, () => _now), tasks, client);
    }

    private const string OneTask = @"[{""description"":""Do it"",""role"":""coding"",""dependsOn"":[]}]";

    private static async Task<AutopilotRunDto> StartFromPhone(AutopilotService svc, FakeLocalCompletionClient client, string device)
    {
        client.EnqueueOk(OneTask);
        return await svc.StartRunAsync("goal", AutopilotPermissionMode.Supervised, CancellationToken.None, device);
    }

    [Fact]
    public async Task A_phone_started_run_is_listed_for_the_desktop_with_its_starter_and_expiry()
    {
        var (svc, _, client) = Build();
        AutopilotRunDto run = await StartFromPhone(svc, client, "device:phone-1");

        AutopilotRunDto listed = Assert.Single(svc.ListPending());
        Assert.Equal(run.Id, listed.Id);
        Assert.Equal("device:phone-1", listed.StartedBy);
        Assert.True(listed.AwaitingDesktopApproval);
        Assert.Equal(_now + AutopilotService.PendingApprovalLifetime, listed.ExpiresUtc);
    }

    [Fact]
    public async Task The_starting_device_can_cancel_its_own_pending_run_and_its_tasks_are_cancelled()
    {
        var (svc, tasks, client) = Build();
        AutopilotRunDto run = await StartFromPhone(svc, client, "device:phone-1");

        Assert.Equal(AutopilotService.CancelOutcome.Cancelled, svc.CancelPendingRun(run.Id, "device:phone-1", false));
        Assert.Equal(AutopilotRunStatus.Cancelled, svc.GetRun(run.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(run.TaskIds[0])!.Status);
        Assert.Empty(svc.ListPending());
        Assert.False(svc.ApproveRun(run.Id));
    }

    [Fact]
    public async Task Another_device_cannot_cancel_a_run_it_did_not_start_but_the_desktop_can()
    {
        var (svc, _, client) = Build();
        AutopilotRunDto run = await StartFromPhone(svc, client, "device:phone-1");

        Assert.Equal(AutopilotService.CancelOutcome.Forbidden, svc.CancelPendingRun(run.Id, "device:phone-2", false));
        Assert.Equal(AutopilotRunStatus.PendingApproval, svc.GetRun(run.Id)!.Status);
        Assert.Equal(AutopilotService.CancelOutcome.Cancelled, svc.CancelPendingRun(run.Id, "desktop", true));
    }

    [Fact]
    public async Task Cancel_reports_not_found_and_not_pending_honestly()
    {
        var (svc, _, client) = Build();
        Assert.Equal(AutopilotService.CancelOutcome.NotFound, svc.CancelPendingRun("missing", "desktop", true));
        client.EnqueueOk(OneTask);
        AutopilotRunDto running = await svc.StartRunAsync("g", AutopilotPermissionMode.Balanced, CancellationToken.None, null);
        Assert.Equal(AutopilotService.CancelOutcome.NotPending, svc.CancelPendingRun(running.Id, "desktop", true));
    }

    [Fact]
    public async Task A_pending_run_expires_after_the_lifetime_with_a_terminal_state_and_cancelled_tasks()
    {
        var (svc, tasks, client) = Build();
        AutopilotRunDto run = await StartFromPhone(svc, client, "device:phone-1");

        _now += AutopilotService.PendingApprovalLifetime - TimeSpan.FromMinutes(1);
        Assert.Equal(0, svc.ExpirePendingRuns());
        Assert.Equal(AutopilotRunStatus.PendingApproval, svc.GetRun(run.Id)!.Status);

        _now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, svc.ExpirePendingRuns());
        Assert.Equal(AutopilotRunStatus.Expired, svc.GetRun(run.Id)!.Status);
        Assert.Equal(AgentTaskStatus.Cancelled, tasks.Get(run.TaskIds[0])!.Status);
        Assert.False(svc.ApproveRun(run.Id));
        Assert.Empty(svc.ListPending());
    }

    [Fact]
    public async Task A_desktop_started_run_has_no_starter()
    {
        var (svc, _, client) = Build();
        client.EnqueueOk(OneTask);
        AutopilotRunDto run = await svc.StartRunAsync("g", AutopilotPermissionMode.Supervised, CancellationToken.None);
        Assert.Null(run.StartedBy);
    }

    [Fact]
    public void The_paired_device_policy_allows_cancel_but_not_the_pending_list_or_approve()
    {
        Assert.True(PairedDeviceEndpointPolicy.IsAllowed("POST", "/api/v1/autopilot/runs/abc/cancel"));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("GET", "/api/v1/autopilot/runs"));
        Assert.False(PairedDeviceEndpointPolicy.IsAllowed("POST", "/api/v1/autopilot/runs/abc/approve"));
    }

    [Fact]
    public void The_browser_session_fixture_denies_the_new_routes()
    {
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "web-session-route-decisions.txt"));
        Assert.Contains("DENY GET /api/v1/autopilot/runs\n", fixture.Replace("\r\n", "\n"));
        Assert.Contains("DENY POST /api/v1/autopilot/runs/{id}/cancel\n", fixture.Replace("\r\n", "\n"));
    }
}

/// <summary>Real-route wiring for the new Autopilot routes.</summary>
public sealed class AutopilotPendingRouteWiringTests : IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private async Task<int> Invoke(string method, string pattern, string owner, string? path = null)
    {
        RouteEndpoint endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == pattern
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        var context = new DefaultHttpContext { RequestServices = _factory.Services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Items["PaRuXOwner"] = owner;
        if (path != null) context.Request.RouteValues["id"] = path;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyDetection());
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task The_pending_list_is_desktop_owner_only()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, await Invoke("GET", "/api/v1/autopilot/runs", "device:paired-id"));
        Assert.Equal(StatusCodes.Status200OK, await Invoke("GET", "/api/v1/autopilot/runs", "desktop"));
    }

    [Fact]
    public async Task Cancelling_an_unknown_run_is_404_for_the_desktop_and_a_device()
    {
        Assert.Equal(StatusCodes.Status404NotFound, await Invoke("POST", "/api/v1/autopilot/runs/{id}/cancel", "desktop", "nope"));
        Assert.Equal(StatusCodes.Status404NotFound, await Invoke("POST", "/api/v1/autopilot/runs/{id}/cancel", "device:paired-id", "nope"));
    }
}

/// <summary>Item 3: the venv's Python minor is read before a locked install.</summary>
public sealed class VenvPythonVersionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-venv-ver-" + Guid.NewGuid().ToString("N"));
    public VenvPythonVersionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string FakeVenv(string? cfg)
    {
        string venv = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(venv, "Scripts"));
        File.WriteAllText(Path.Combine(venv, "Scripts", "python.exe"), "");
        if (cfg != null) File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), cfg);
        return venv;
    }

    [Fact]
    public void A_venv_of_another_minor_is_recreated_only_when_the_version_was_read_and_differs()
    {
        string old = FakeVenv("home = C:\\x\r\nversion = 3.12.4\r\n");
        Assert.Equal("3.12", ManagedPythonRuntime.ReadVenvPythonMinor(old));
        Assert.True(ManagedPythonRuntime.ShouldRecreateVenvForVersion(old, "3.11.9"));
    }

    [Fact]
    public void A_matching_venv_is_kept()
    {
        string ok = FakeVenv("version = 3.11.9\r\n");
        Assert.False(ManagedPythonRuntime.ShouldRecreateVenvForVersion(ok, "3.11.9"));
        Assert.False(ManagedPythonRuntime.ShouldRecreateVenvForVersion(FakeVenv("version_info = 3.11.2.final.0\r\n"), "3.11.9"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("home = C:\\x\r\n")]
    [InlineData("version = not-a-version\r\n")]
    public void An_unreadable_version_never_triggers_a_delete(string? cfg)
    {
        string venv = FakeVenv(cfg);
        Assert.Null(ManagedPythonRuntime.ReadVenvPythonMinor(venv));
        Assert.False(ManagedPythonRuntime.ShouldRecreateVenvForVersion(venv, "3.11.9"));
    }

    [Fact]
    public async Task A_locked_install_is_refused_in_a_venv_of_the_wrong_python_minor()
    {
        string venv = FakeVenv("version = 3.12.4\r\n");
        ManagedPipRefusalException error = await Assert.ThrowsAsync<ManagedPipRefusalException>(() =>
            ManagedPythonRuntime.PipInstallAsync(venv, ManagedRequirementLocks.ArgumentsFor("cutout"), CancellationToken.None));
        Assert.Equal("python_version_mismatch", error.Code);
    }
}

/// <summary>Item 4: no lock may hold two distributions that provide the same import package.</summary>
public sealed class ManagedLockLintTests
{
    // Distributions that install the same top-level import package and overwrite each other under --no-deps.
    private static readonly string[][] Conflicts =
    {
        new[] { "onnxruntime", "onnxruntime-gpu", "onnxruntime-directml", "onnxruntime-openvino", "onnxruntime-training" },
        new[] { "opencv-python", "opencv-contrib-python", "opencv-python-headless", "opencv-contrib-python-headless" },
        new[] { "pillow", "pillow-simd" },
        new[] { "tensorflow", "tensorflow-cpu", "tensorflow-gpu", "tf-nightly" },
        new[] { "faiss-cpu", "faiss-gpu" },
        new[] { "torch", "torch-directml" },
        new[] { "pycryptodome", "pycryptodomex" }
    };

    private static string Normalize(string name) => name.ToLowerInvariant().Replace('_', '-').Replace('.', '-');

    internal static IReadOnlyList<string> Distributions(string lockPath) =>
        File.ReadAllLines(lockPath)
            .Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && l[0] != '#' && l[0] != '-')
            .Select(l => Normalize(new string(l.TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray())))
            .ToList();

    internal static IReadOnlyList<string> FindConflicts(IEnumerable<string> distributions)
    {
        HashSet<string> present = distributions.ToHashSet();
        return Conflicts.Select(group => group.Where(present.Contains).ToList())
            .Where(found => found.Count > 1).Select(found => string.Join(" + ", found)).ToList();
    }

    [Theory]
    [InlineData("cutout")]
    [InlineData("lora-convert")]
    [InlineData("threed")]
    [InlineData("diffusers-engine")]
    [InlineData("speech")]
    [InlineData("avatar")]
    [InlineData("torch-cu128")]
    [InlineData("torch-cpu")]
    public void A_shipped_lock_has_one_distribution_per_import_package(string set) =>
        Assert.Empty(FindConflicts(Distributions(ManagedRequirementLocks.PathFor(set))));

    [Fact]
    public void The_lint_detects_the_known_conflicts()
    {
        Assert.Single(FindConflicts(new[] { "onnxruntime", "onnxruntime-gpu", "numpy" }));
        Assert.Single(FindConflicts(new[] { "opencv-python", "opencv-contrib-python" }));
        Assert.Single(FindConflicts(new[] { "pillow", "pillow-simd" }));
        Assert.Empty(FindConflicts(new[] { "onnxruntime-gpu", "opencv-python", "pillow" }));
    }

    [Fact]
    public void The_avatar_lock_follows_the_pinned_latentsync_requirements()
    {
        string text = File.ReadAllText(ManagedRequirementLocks.PathFor("avatar"));
        Assert.Contains("insightface==0.7.3 ", text);          // LatentSync's own pin, not the 2.x rewrite
        Assert.Contains("onnxruntime-gpu==1.21.0 ", text);
        Assert.Contains("opencv-python==4.9.0.80 ", text);
        Assert.DoesNotContain("\nonnxruntime==", text);
        Assert.DoesNotContain("opencv-contrib-python==", text);
        Assert.DoesNotContain("opencv-python-headless==", text);
    }
}

/// <summary>Item 5: source-archive promotion, collisions and owned-root reparse checks.</summary>
public sealed class ManagedSourceArchiveRound3Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-src-r3-" + Guid.NewGuid().ToString("N"));
    public ManagedSourceArchiveRound3Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Zip(string name, params (string, string)[] entries)
    {
        string path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string n, string c) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(n).Open());
            w.Write(c);
        }
        return path;
    }

    [Fact]
    public async Task Case_colliding_entries_are_skipped_and_reported_and_the_first_file_is_kept()
    {
        string archive = Zip("case.zip", ("repo/README.md", "first"), ("repo/readme.md", "second"), ("repo/ok.txt", "ok"));
        string destination = Path.Combine(_root, "case-out");

        IReadOnlyList<string> skipped = await ManagedSourceArchive.ExtractArchiveAsync(archive, destination, CancellationToken.None);

        Assert.Single(skipped);
        Assert.Contains("readme.md", skipped[0]);
        Assert.Equal("first", File.ReadAllText(Path.Combine(destination, "README.md")));
        Assert.Equal("ok", File.ReadAllText(Path.Combine(destination, "ok.txt")));
    }

    [Fact]
    public async Task Traversal_is_still_refused_in_the_lenient_source_mode()
    {
        string archive = Zip("trav.zip", ("repo/ok.txt", "ok"), ("repo/../../escape.txt", "bad"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedSourceArchive.ExtractArchiveAsync(archive, Path.Combine(_root, "trav-out"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task A_data_root_under_a_junction_still_works_because_only_the_owned_tree_is_checked()
    {
        string real = Path.Combine(_root, "real");
        string linked = Path.Combine(_root, "linked");
        Directory.CreateDirectory(real);
        if (!MakeJunction(linked, real)) return;
        string archive = Zip("junction-root.zip", ("repo/a.txt", "a"));

        await ManagedSourceArchive.ExtractArchiveAsync(archive, Path.Combine(linked, "managed-src", "x"), CancellationToken.None);

        Assert.Equal("a", File.ReadAllText(Path.Combine(real, "managed-src", "x", "a.txt")));
    }

    [Fact]
    public async Task The_default_extractor_still_rejects_a_junction_in_the_destination_ancestors()
    {
        string real = Path.Combine(_root, "real2");
        string linked = Path.Combine(_root, "linked2");
        Directory.CreateDirectory(real);
        if (!MakeJunction(linked, real)) return;
        string archive = Zip("anc.zip", ("a.txt", "a"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, Path.Combine(linked, "out"), CancellationToken.None));
    }

    [Fact]
    public void Promote_replaces_the_previous_tree_and_removes_the_old_copy()
    {
        string dest = Path.Combine(_root, "dest");
        string fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "old.txt"), "old");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "new.txt"), "new");

        ManagedSourceArchive.Promote(fresh, dest);

        Assert.True(File.Exists(Path.Combine(dest, "new.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "old.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "dest.old-*"));
    }

    [Fact]
    public void Promote_rolls_back_to_the_previous_tree_when_the_new_tree_cannot_be_moved_in()
    {
        string dest = Path.Combine(_root, "dest2");
        string missingNew = Path.Combine(_root, "does-not-exist");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "old.txt"), "old");

        Assert.ThrowsAny<IOException>(() => ManagedSourceArchive.Promote(missingNew, dest));

        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "old.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "dest2.old-*"));
    }

    private static bool MakeJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var info = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using Process p = Process.Start(info)!;
            p.WaitForExit(15000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Items 6 and 7.</summary>
public sealed class WorkspaceAndGameArchiveRound3Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-r3-" + Guid.NewGuid().ToString("N"));
    public WorkspaceAndGameArchiveRound3Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("Vault")]
    [InlineData("wallet")]
    [InlineData("Ethereum")]
    [InlineData("Credentials")]
    public void A_project_folder_named_like_a_credential_store_outside_the_profile_is_allowed(string name)
    {
        string project = Path.Combine(_root, "dev", name);
        Directory.CreateDirectory(project);
        string profile = Path.Combine(_root, "profile");
        AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(project, dataRoot: Path.Combine(_root, "data"),
            userProfile: profile, credentialLocations: new[] { profile });
    }

    [Fact]
    public void A_wallets_folder_under_appdata_is_refused()
    {
        string appData = Path.Combine(_root, "AppData", "Roaming");
        string wallets = Path.Combine(appData, "SomeApp", "wallets");
        Directory.CreateDirectory(wallets);
        Assert.Throws<InvalidDataException>(() => AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(wallets,
            dataRoot: Path.Combine(_root, "data"), userProfile: Path.Combine(_root, "profile"), credentialLocations: new[] { appData }));
    }

    [Fact]
    public void A_dot_ssh_folder_is_refused_wherever_it_lives()
    {
        string ssh = Path.Combine(_root, "dev", ".ssh");
        Directory.CreateDirectory(ssh);
        Assert.Throws<InvalidDataException>(() => AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(ssh,
            dataRoot: Path.Combine(_root, "data"), userProfile: Path.Combine(_root, "profile"), credentialLocations: new[] { Path.Combine(_root, "profile") }));
    }

    private string BigZip(int megabytes)
    {
        string path = Path.Combine(_root, "game.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using Stream s = zip.CreateEntry("game/assets.pak", CompressionLevel.SmallestSize).Open();
        s.Write(new byte[megabytes * 1024 * 1024]);
        return path;
    }

    [Fact]
    public async Task A_highly_compressible_game_archive_is_not_called_a_bomb_when_the_ratio_is_not_enforced()
    {
        string archive = BigZip(80);
        string destination = Path.Combine(_root, "game-out");

        await SafeArchiveExtractor.ExtractAsync(archive, destination, CancellationToken.None,
            maxExtractedBytes: 1L << 32, maxEntries: 200_000, enforceCompressionRatio: false);

        Assert.Equal(80L * 1024 * 1024, new FileInfo(Path.Combine(destination, "game", "assets.pak")).Length);
    }

    [Fact]
    public async Task The_real_byte_budget_still_applies_without_the_ratio()
    {
        string archive = BigZip(8);
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeArchiveExtractor.ExtractAsync(archive,
            Path.Combine(_root, "budget-out"), CancellationToken.None, maxExtractedBytes: 1024 * 1024, enforceCompressionRatio: false));
    }

    [Fact]
    public async Task The_ratio_refusal_tells_the_user_what_to_do()
    {
        string archive = BigZip(80);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SafeArchiveExtractor.ExtractAsync(archive, Path.Combine(_root, "ratio-out"), CancellationToken.None));
        Assert.Contains("extract it yourself", error.Message);
    }

    [Fact]
    public async Task Game_archives_keep_traversal_protection()
    {
        string archive = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("../x.exe").Open())) w.Write("x");
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeArchiveExtractor.ExtractAsync(archive,
            Path.Combine(_root, "evil-out"), CancellationToken.None, maxEntries: 200_000, enforceCompressionRatio: false));
    }
}

/// <summary>Items 8, 9 and 10.</summary>
public sealed class StrictLockShapeAndPinsRound3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-r3b-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousPins = RuntimeDigestPins.Path;
    public StrictLockShapeAndPinsRound3Tests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        RuntimeDigestPins.Path = _previousPins;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Lock() => ManagedRequirementLocks.PathFor("cutout");

    [Theory]
    [InlineData("--no-verify-hashes")]
    [InlineData("--constraint")]
    [InlineData("--override")]
    [InlineData("--upgrade")]
    public void Extra_flags_on_a_locked_install_are_refused(string flag)
    {
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps", flag, "x.txt", "-r", Lock() }));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps", "-r", Lock(), flag }));
    }

    [Fact]
    public void A_second_requirements_file_or_a_reordered_shape_is_refused()
    {
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps", "-r", Lock(), "-r", Lock() }));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--no-deps", "--require-hashes", "-r", Lock() }));
    }

    [Fact]
    public void The_exact_known_shapes_are_accepted()
    {
        string[] plain = { "--require-hashes", "--no-deps", "-r", Lock() };
        Assert.Equal(plain, ManagedPipInstallPolicy.Evaluate(plain));
        string[] withIndex = { "--require-hashes", "--no-deps", "--index-url", TorchInstallChannelPolicy.CpuTorchIndexUrl, "-r", ManagedRequirementLocks.PathFor("torch-cpu") };
        Assert.Equal(withIndex, ManagedPipInstallPolicy.Evaluate(withIndex));
    }

    [Fact]
    public void The_exllama_wheel_set_is_exactly_one_existing_local_whl()
    {
        string wheel = Path.Combine(_dir, "exllamav3-1.0-cp311-win_amd64.whl");
        File.WriteAllText(wheel, "x");
        Assert.Equal(new[] { wheel }, ManagedPipInstallPolicy.Evaluate(new[] { wheel }, "exllamav3-wheel"));

        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(new[] { Path.Combine(_dir, "missing.whl") }, "exllamav3-wheel"));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(new[] { wheel, "requests" }, "exllamav3-wheel"));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(new[] { wheel, "--index-url", "https://evil.example" }, "exllamav3-wheel"));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(new[] { "--pre", wheel }, "exllamav3-wheel"));
        string notWheel = Path.Combine(_dir, "pkg.tar.gz");
        File.WriteAllText(notWheel, "x");
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(new[] { notWheel }, "exllamav3-wheel"));
    }

    private static RuntimeReleaseAsset Asset() => new("llama-b9999-bin-win-cpu-x64.zip",
        "https://github.com/ggml-org/llama.cpp/releases/download/b9999/llama-b9999-bin-win-cpu-x64.zip", 1024, "sha256:" + new string('a', 64));

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("[]")]
    public void An_existing_but_unparsable_pin_file_fails_closed(string content)
    {
        string path = Path.Combine(_dir, "pins.json");
        File.WriteAllText(path, content);
        RuntimeDigestPins.Path = path;
        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateAsset(Asset(), "ggml-org", "llama.cpp"));
    }

    [Fact]
    public void A_malformed_pin_for_the_requested_asset_fails_closed()
    {
        string path = Path.Combine(_dir, "pins.json");
        File.WriteAllText(path, "{\"pins\":[{\"repository\":\"ggml-org/llama.cpp\",\"asset\":\"llama-b9999-bin-win-cpu-x64.zip\",\"sha256\":\"nothex\"}]}");
        RuntimeDigestPins.Path = path;
        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateAsset(Asset(), "ggml-org", "llama.cpp"));
    }

    [Fact]
    public void A_missing_pin_file_is_the_documented_no_pins_case()
    {
        RuntimeDigestPins.Path = Path.Combine(_dir, "absent.json");
        RuntimeArtifactSecurityPolicy.ValidateAsset(Asset(), "ggml-org", "llama.cpp");
    }

    [Fact]
    public void A_user_root_such_as_documents_is_never_given_a_new_acl()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents) || !Directory.Exists(documents)) return;
        Assert.Null(ServerState.ProtectStateDirectoryIfShared(documents, Path.Combine(_dir, "other-profile")));
        Assert.Null(ServerState.ProtectStateDirectoryIfShared(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.Combine(_dir, "other-profile")));
    }
}
