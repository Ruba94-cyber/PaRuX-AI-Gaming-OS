using PaRuX.Server;
using PaRuX.Server.Knowledge;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F08: the state folder is restricted to the current user when it sits outside the user profile.</summary>
public sealed class ServerStateFolderProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-state-prot-" + Guid.NewGuid().ToString("N"));

    public ServerStateFolderProtectionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void A_state_folder_inside_the_user_profile_is_left_alone()
    {
        string state = Path.Combine(_root, "profile", "PaRuX", "server");
        Directory.CreateDirectory(state);
        Assert.False(ServerState.ProtectStateDirectoryIfShared(state, Path.Combine(_root, "profile")));
    }

    [Fact]
    public void A_state_folder_outside_the_user_profile_is_restricted_to_the_current_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        string state = Path.Combine(_root, "shared", "server");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "bootstrap.json"), "{}");

        Assert.True(ServerState.ProtectStateDirectoryIfShared(state, Path.Combine(_root, "someone-elses-profile")));
        Assert.True(LocalFileProtection.IsRestricted(state));
        Assert.True(File.Exists(Path.Combine(state, "bootstrap.json")));
    }

    [Fact]
    public void A_missing_state_folder_is_not_created_or_restricted()
    {
        Assert.False(ServerState.ProtectStateDirectoryIfShared(Path.Combine(_root, "absent"), Path.Combine(_root, "profile")));
        Assert.False(Directory.Exists(Path.Combine(_root, "absent")));
    }
}

/// <summary>SEC-F15: a coding workspace root cannot be a drive root, the whole profile or a sensitive location.</summary>
public sealed class WorkspaceRootSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-ws-root-" + Guid.NewGuid().ToString("N"));

    public WorkspaceRootSafetyTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void A_drive_root_is_rejected()
    {
        string driveRoot = Path.GetPathRoot(_root)!;
        Assert.Throws<InvalidDataException>(() => AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(driveRoot));
    }

    [Fact]
    public void The_whole_user_profile_is_rejected()
    {
        string profile = Path.Combine(_root, "Users", "someone");
        Directory.CreateDirectory(profile);
        Assert.Throws<InvalidDataException>(() =>
            AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(profile, dataRoot: Path.Combine(_root, "data"), userProfile: profile));
    }

    [Fact]
    public void A_project_folder_inside_the_profile_is_accepted()
    {
        string profile = Path.Combine(_root, "Users", "someone");
        string project = Path.Combine(profile, "projects", "demo");
        Directory.CreateDirectory(project);
        AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(project, dataRoot: Path.Combine(_root, "data"), userProfile: profile);
    }

    [Fact]
    public void PaRuX_own_data_folder_is_rejected()
    {
        string data = Path.Combine(_root, "data");
        string inside = Path.Combine(data, "server");
        Directory.CreateDirectory(inside);
        Assert.Throws<InvalidDataException>(() =>
            AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(inside, dataRoot: data, userProfile: Path.Combine(_root, "p")));
    }

    [Fact]
    public void A_credential_store_folder_is_rejected()
    {
        string ssh = Path.Combine(_root, "home", ".ssh");
        Directory.CreateDirectory(ssh);
        Assert.Throws<InvalidDataException>(() =>
            AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(ssh, dataRoot: Path.Combine(_root, "data"), userProfile: Path.Combine(_root, "p")));
    }

    [Fact]
    public void The_windows_folder_is_rejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidDataException>(() => AgentRepositoryAuthoritySetStore.RejectUnsafeWorkspaceRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), dataRoot: Path.Combine(_root, "data")));
    }
}

/// <summary>SEC-F12: a reviewed digest pin shipped with PaRuX outranks the digest GitHub advertises.</summary>
public sealed class RuntimeDigestPinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-pins-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = RuntimeDigestPins.Path;
    private const string AssetName = "llama-b9999-bin-win-cuda-cu12.4-x64.zip";

    public RuntimeDigestPinTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        RuntimeDigestPins.Path = _previous;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static RuntimeReleaseAsset Asset(string digestHex) => new(
        AssetName,
        "https://github.com/ggml-org/llama.cpp/releases/download/b9999/" + AssetName,
        1024, "sha256:" + digestHex);

    private void Pin(string digestHex)
    {
        string path = Path.Combine(_dir, "pins.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"pins\":[{\"repository\":\"ggml-org/llama.cpp\",\"asset\":\""
            + AssetName + "\",\"sha256\":\"" + digestHex + "\"}]}");
        RuntimeDigestPins.Path = path;
    }

    [Fact]
    public void An_advertised_digest_that_differs_from_the_pin_is_refused()
    {
        Pin(new string('a', 64));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            RuntimeArtifactSecurityPolicy.ValidateAsset(Asset(new string('b', 64)), "ggml-org", "llama.cpp"));
        Assert.Contains("reviewed pin", error.Message);
    }

    [Fact]
    public void An_advertised_digest_equal_to_the_pin_is_accepted_regardless_of_case()
    {
        Pin(new string('a', 64));
        RuntimeArtifactSecurityPolicy.ValidateAsset(Asset(new string('A', 64)), "ggml-org", "llama.cpp");
    }

    [Fact]
    public void An_asset_without_a_pin_keeps_the_github_digest_rule()
    {
        Pin(new string('a', 64));
        RuntimeReleaseAsset other = Asset(new string('c', 64)) with
        {
            Name = "llama-b9998-bin-win-cuda-cu12.4-x64.zip",
            DownloadUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b9998/llama-b9998-bin-win-cuda-cu12.4-x64.zip"
        };
        RuntimeArtifactSecurityPolicy.ValidateAsset(other, "ggml-org", "llama.cpp");
    }

    [Fact]
    public void A_missing_digest_is_still_refused_even_when_a_pin_exists()
    {
        Pin(new string('a', 64));
        Assert.Throws<InvalidDataException>(() => RuntimeArtifactSecurityPolicy.ValidateAsset(
            Asset(new string('a', 64)) with { Digest = null }, "ggml-org", "llama.cpp"));
    }

    [Fact]
    public void The_shipped_pin_file_parses_and_pins_only_well_formed_digests()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, RuntimeDigestPins.FileName);
        Assert.True(File.Exists(shipped));
        RuntimeDigestPins.Path = shipped;
        Assert.Equal("dafb19ac757bfb7cfc1f91c6e26703264df861c4331e3ef6dc9543ae8d010c5e",
            RuntimeDigestPins.Find("ggml-org", "llama.cpp", "llama-b12283-bin-win-cpu-x64.zip"));
        Assert.Null(RuntimeDigestPins.Find("ggml-org", "llama.cpp", "llama-b1-bin-win-cpu-x64.zip"));
    }
}
