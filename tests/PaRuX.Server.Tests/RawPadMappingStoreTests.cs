using PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RawPadMappingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-raw-map-" + Guid.NewGuid().ToString("N"));
    private readonly RawPadMappingStore _store;

    public RawPadMappingStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new RawPadMappingStore(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static PadMapping Mapping() => new()
    {
        Buttons = new() { [0] = Xbox360Report.A, [1] = Xbox360Report.B },
        LeftXAxis = 0, LeftYAxis = 1, RightXAxis = 2, RightYAxis = 3,
        LeftTriggerAxis = 4, TriggerAxisIsSigned = true, StickDeadzone = 0.12
    };

    [Fact]
    public void Saved_mapping_round_trips_without_exposing_device_identity()
    {
        const string identity = "private-device-012345";
        Assert.Null(_store.Load(identity, out string? missing));
        Assert.Null(missing);
        Assert.True(_store.Save(identity, Mapping(), out string? saveError), saveError);
        var files = Directory.GetFiles(_root);
        Assert.Single(files);
        Assert.DoesNotContain("private-device", files[0]);
        string content = File.ReadAllText(files[0]);
        Assert.DoesNotContain(identity, content);

        PadMapping? loaded = _store.Load(identity, out string? error);
        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(Xbox360Report.A, loaded!.Buttons[0]);
        Assert.Equal(0.12, loaded.StickDeadzone, precision: 4);
        Assert.True(loaded.TriggerAxisIsSigned);
    }

    [Fact]
    public void Each_controller_has_independent_mapping()
    {
        const string first = "device-one";
        const string second = "device-two";
        Assert.True(_store.Save(first, Mapping(), out _));
        var alternate = new PadMapping { Buttons = new() { [5] = Xbox360Report.Y } };
        Assert.True(_store.Save(second, alternate, out _));
        Assert.Equal(Xbox360Report.A, _store.Load(first, out _)!.Buttons[0]);
        Assert.Equal(Xbox360Report.Y, _store.Load(second, out _)!.Buttons[5]);
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void Invalid_mapping_never_overwrites_a_valid_profile()
    {
        const string id = "known-physical-controller";
        Assert.True(_store.Save(id, Mapping(), out _));
        string profile = Assert.Single(Directory.GetFiles(_root));
        string original = File.ReadAllText(profile);
        var invalid = Mapping();
        invalid.Buttons[-1] = Xbox360Report.X;
        Assert.False(_store.Save(id, invalid, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(original, File.ReadAllText(profile));
    }

    [Fact]
    public void A_corrupt_stored_profile_is_preserved_and_blocks_an_overwrite()
    {
        const string id = "controller-with-corrupt-json";
        Assert.True(_store.Save(id, Mapping(), out _));
        string file = Assert.Single(Directory.GetFiles(_root));
        File.WriteAllText(file, "{broken");
        Assert.Null(_store.Load(id, out string? loadError));
        Assert.NotNull(loadError);
        Assert.False(_store.Save(id, Mapping(), out string? saveError));
        Assert.NotNull(saveError);
        Assert.Equal("{broken", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad\nidentifier")]
    public void Invalid_device_identity_cannot_write_a_profile(string id)
    {
        Assert.False(_store.Save(id, Mapping(), out _));
        Assert.Empty(Directory.GetFiles(_root));
    }
}
