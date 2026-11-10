using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PaRuXLocalAiStateRootTests : IDisposable
{
    private const string OverrideName = "PARUX_LOCALAI_STATE_DIR";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideName);

    [Fact]
    public void Absolute_process_override_is_normalized_and_used()
    {
        string overrideRoot = Path.Combine(Path.GetTempPath(), "parux-localai-state", "..", "isolated-state");
        Environment.SetEnvironmentVariable(OverrideName, overrideRoot);

        Assert.Equal(Path.GetFullPath(overrideRoot), PaRuXLocalAiStateRoot.Path);
    }

    [Fact]
    public void Relative_process_override_is_ignored()
    {
        Environment.SetEnvironmentVariable(OverrideName, "relative-state");

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAI");
        Assert.Equal(expected, PaRuXLocalAiStateRoot.Path);
    }

    [Fact]
    public void Extracted_portable_marker_wins_over_an_unrelated_host_state_override()
    {
        string root = Path.Combine(Path.GetTempPath(), "localai-portable-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "PaRuXData"));
            Directory.CreateDirectory(Path.Combine(root, "PaRuX.Server"));
            File.WriteAllText(Path.Combine(root, PaRuXDataRoot.PortableMarkerFileName), "Standard Portable");
            string unrelatedHostState = Path.Combine(Path.GetTempPath(), "host-localai-data");
            Assert.Equal(Path.Combine(root, "PaRuXData"),
                PaRuXLocalAiStateRoot.Resolve(root, unrelatedHostState));
            Assert.Equal(Path.Combine(root, "PaRuXData"),
                PaRuXLocalAiStateRoot.Resolve(Path.Combine(root, "PaRuX.Server"), unrelatedHostState));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    public void Dispose() => Environment.SetEnvironmentVariable(OverrideName, _previousOverride);
}
