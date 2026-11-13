using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>SEC-F02: managed Python installs are hash-locked or refused before any process starts.</summary>
public sealed class ManagedPipInstallPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-piplock-" + Guid.NewGuid().ToString("N"));
    private static readonly string Hash = new('a', 64);

    public ManagedPipInstallPolicyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteLock(string content, string name = "set.lock.txt")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string GoodLock() => WriteLock($"pkg==1.0 \\\n    --hash=sha256:{Hash}\n    # via x\n");

    [Theory]
    [InlineData("transformers")]
    [InlineData("gguf>=0.10")]
    [InlineData("-r")]
    public void Unpinned_requirement_set_is_refused_before_any_install(string requirement)
    {
        ManagedPipRefusalException error = Assert.Throws<ManagedPipRefusalException>(
            () => ManagedPipInstallPolicy.Evaluate(new[] { requirement }));
        Assert.Equal(ManagedPipInstallPolicy.UnlockedCode, error.Code);
    }

    [Fact]
    public async Task PipInstallAsync_refuses_an_unlocked_set_without_starting_a_process()
    {
        // The venv does not exist: a refusal must come from the policy, not from a missing interpreter.
        ManagedPipRefusalException error = await Assert.ThrowsAsync<ManagedPipRefusalException>(() =>
            ManagedPythonRuntime.PipInstallAsync(Path.Combine(_dir, "no-venv"),
                new[] { "transformers", "peft" }, CancellationToken.None));
        Assert.Equal(ManagedPipInstallPolicy.UnlockedCode, error.Code);
    }

    [Fact]
    public void Only_a_registered_unlocked_set_may_install_unpinned_and_unknown_ids_are_refused()
    {
        string[] wheel = { Path.Combine(_dir, "exllamav3.whl") };
        File.WriteAllText(wheel[0], "x");
        Assert.Equal(wheel, ManagedPipInstallPolicy.Evaluate(wheel, "exllamav3-wheel"));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(wheel, "made-up-set"));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(wheel, null));
    }

    [Fact]
    public void A_valid_hash_lock_is_accepted_with_require_hashes_and_no_deps()
    {
        string lockPath = GoodLock();
        string[] args = { "--require-hashes", "--no-deps", "-r", lockPath };
        Assert.Equal(args, ManagedPipInstallPolicy.Evaluate(args));
    }

    [Theory]
    [InlineData("pkg>=1.0 \\\n    --hash=sha256:AAAA\n")]                       // range, short hash
    [InlineData("pkg==1.0\n")]                                                   // no hash at all
    [InlineData("--index-url https://evil.example/simple\npkg==1.0 --hash=sha256:HASH\n")] // option line
    [InlineData("-e git+https://evil.example/x.git\n")]                           // editable
    [InlineData("pkg==1.0 --hash=sha1:HASH\n")]                                   // wrong algorithm
    [InlineData("# only comments\n")]                                             // empty set
    public void A_lock_with_an_unpinned_unhashed_or_option_line_is_refused(string content)
    {
        string lockPath = WriteLock(content.Replace("HASH", Hash));
        ManagedPipRefusalException error = Assert.Throws<ManagedPipRefusalException>(
            () => ManagedRequirementLocks.Validate(lockPath));
        Assert.Equal(ManagedRequirementLocks.InvalidCode, error.Code);
        Assert.Throws<ManagedPipRefusalException>(() =>
            ManagedPipInstallPolicy.Evaluate(new[] { "--require-hashes", "--no-deps", "-r", lockPath }));
    }

    [Fact]
    public void A_missing_lock_file_is_refused_with_a_clear_code()
    {
        ManagedPipRefusalException error = Assert.Throws<ManagedPipRefusalException>(
            () => ManagedRequirementLocks.Validate(Path.Combine(_dir, "absent.lock.txt")));
        Assert.Equal(ManagedRequirementLocks.MissingCode, error.Code);
    }

    [Fact]
    public void A_locked_install_cannot_smuggle_in_an_extra_index_or_drop_no_deps()
    {
        string lockPath = GoodLock();
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps", "--extra-index-url", "https://evil.example/simple", "-r", lockPath }));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps", "--index-url", "https://evil.example/simple", "-r", lockPath }));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "-r", lockPath }));
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "--require-hashes", "--no-deps" }));
    }

    [Theory]
    [InlineData("https://download.pytorch.org/whl/cu128", "torch-cu128")]
    [InlineData("https://download.pytorch.org/whl/cpu", "torch-cpu")]
    public void The_channel_torch_pin_resolves_to_its_hashed_closure(string index, string set)
    {
        IReadOnlyList<string> args = ManagedPipInstallPolicy.Evaluate(
            new[] { "torch==" + ExLlamaV3BuildCatalog.TorchVersion, "--index-url", index });
        Assert.Contains("--require-hashes", args);
        Assert.Contains("--no-deps", args);
        Assert.Equal(ManagedRequirementLocks.PathFor(set), args[^1]);
        Assert.True(ManagedRequirementLocks.Validate(args[^1]) > 0);
    }

    [Fact]
    public void An_unknown_torch_index_is_refused()
    {
        Assert.Throws<ManagedPipRefusalException>(() => ManagedPipInstallPolicy.Evaluate(
            new[] { "torch==" + ExLlamaV3BuildCatalog.TorchVersion, "--index-url", "https://evil.example/whl" }));
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
    public void Every_shipped_lock_validates_and_is_installed_with_hash_enforcement(string set)
    {
        Assert.True(ManagedRequirementLocks.Validate(ManagedRequirementLocks.PathFor(set)) > 0);
        IReadOnlyList<string> args = ManagedRequirementLocks.ArgumentsFor(set);
        Assert.Equal(args, ManagedPipInstallPolicy.Evaluate(args));
    }

    [Fact]
    public void The_diffusers_lock_pins_exactly_the_versions_the_engine_constants_declare()
    {
        string text = File.ReadAllText(ManagedRequirementLocks.PathFor("diffusers-engine"));
        Assert.Contains("diffusers==" + ManagedDiffusersImageEngine.DiffusersVersion + " ", text);
        Assert.Contains("transformers==" + ManagedDiffusersImageEngine.TransformersVersion + " ", text);
        Assert.Contains("accelerate==" + ManagedDiffusersImageEngine.AccelerateVersion + " ", text);
    }

    [Fact]
    public void The_torch_locks_pin_the_declared_torch_build_per_channel()
    {
        Assert.Contains("torch==" + ExLlamaV3BuildCatalog.TorchVersion + "+cu128 ",
            File.ReadAllText(ManagedRequirementLocks.PathFor("torch-cu128")));
        Assert.Contains("torch==" + ExLlamaV3BuildCatalog.TorchVersion + "+cpu ",
            File.ReadAllText(ManagedRequirementLocks.PathFor("torch-cpu")));
    }

    [Fact]
    public void The_speech_lock_hashes_the_url_pinned_spacy_model_wheel()
    {
        string text = File.ReadAllText(ManagedRequirementLocks.PathFor("speech"));
        int at = text.IndexOf("en-core-web-sm @ https://", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("--hash=sha256:", text[at..(at + 400)]);
    }
}
