using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PortableLocalAiRootPolicyTests
{
    private static readonly string DataRoot = Path.Combine(Path.GetTempPath(), "parux-portable-test");
    private static readonly string Legacy = Path.Combine(Path.GetTempPath(), "Documents", "LocalAI");

    [Fact]
    public void Explicit_portable_root_does_not_adopt_existing_legacy_models_or_runtimes()
    {
        Assert.Equal(Path.Combine(DataRoot, "LocalAI"),
            PortableLocalAiRootPolicy.Resolve(DataRoot, DataRoot, Legacy, legacyPopulated: true));
    }

    [Fact]
    public void Ordinary_user_profile_still_reuses_existing_legacy_installation()
    {
        Assert.Equal(Legacy, PortableLocalAiRootPolicy.Resolve(DataRoot, null, Legacy, legacyPopulated: true));
    }

    [Fact]
    public void Fresh_normal_profile_falls_back_to_its_local_data_root()
    {
        Assert.Equal(Path.Combine(DataRoot, "LocalAI"),
            PortableLocalAiRootPolicy.Resolve(DataRoot, null, Legacy, legacyPopulated: false));
    }

    [Fact]
    public void Relative_or_whitespace_data_root_is_not_mistaken_for_portable_isolation()
    {
        Assert.Equal(Legacy,
            PortableLocalAiRootPolicy.Resolve(DataRoot, "relative\\folder", Legacy, legacyPopulated: true));
        Assert.Equal(Legacy,
            PortableLocalAiRootPolicy.Resolve(DataRoot, " ", Legacy, legacyPopulated: true));
    }
}
