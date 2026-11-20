using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class XInputShimInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-shim-inst-" + Guid.NewGuid().ToString("N"));
    private string Shims => Path.Combine(_root, "shims");
    private string State => Path.Combine(_root, "state");
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private XInputShimInstaller NewInstaller()
    {
        foreach (string arch in new[] { "x64", "x86" })
        {
            Directory.CreateDirectory(Path.Combine(Shims, arch));
            File.WriteAllBytes(Path.Combine(Shims, arch, "parux_xinput.dll"), System.Text.Encoding.ASCII.GetBytes("shim-" + arch + new string('x', 400)));
        }
        return new XInputShimInstaller(Shims, State);
    }

    /// <summary>A minimal PE header is enough: "MZ", a pointer at 0x3C, then "PE\0\0" and the machine type.</summary>
    private string Game(string name, ushort machine, params string[] extra)
    {
        string dir = Path.Combine(_root, "games", name);
        Directory.CreateDirectory(dir);
        var exe = new byte[2048];
        exe[0] = (byte)'M'; exe[1] = (byte)'Z';
        BitConverter.GetBytes(128).CopyTo(exe, 0x3C);
        exe[128] = (byte)'P'; exe[129] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(exe, 132);
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), exe);
        foreach (string e in extra) File.WriteAllBytes(Path.Combine(dir, e), new byte[10]);
        return dir;
    }

    [Fact]
    public void It_installs_the_matching_architecture_records_the_hash_and_removes_exactly_that()
    {
        XInputShimInstaller installer = NewInstaller();
        string game = Game("g64", 0x8664);

        XInputShimResult installed = installer.Install(game);

        Assert.True(installed.Ok, installed.Message);
        Assert.Equal("x64", installed.Install!.Architecture);
        foreach (string name in XInputShimInstaller.ShimNames)
            Assert.StartsWith("shim-x64", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(game, name))));
        Assert.Single(installer.List());

        XInputShimResult removed = installer.Uninstall(game);
        Assert.True(removed.Ok, removed.Message);
        foreach (string name in XInputShimInstaller.ShimNames) Assert.False(File.Exists(Path.Combine(game, name)));
        Assert.True(File.Exists(Path.Combine(game, "game.exe")));
        Assert.Empty(installer.List());
    }

    [Fact]
    public void A_32_bit_game_gets_the_32_bit_shim()
    {
        XInputShimInstaller installer = NewInstaller();
        string game = Game("g32", 0x014C);
        Assert.Equal("x86", installer.Install(game).Install!.Architecture);
        Assert.StartsWith("shim-x86", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(game, "xinput1_4.dll"))));
    }

    [Theory]
    [InlineData("EasyAntiCheat")]
    [InlineData("BEService.exe")]
    [InlineData("vgk.sys")]
    public void A_game_with_an_anti_cheat_is_refused_and_nothing_is_written(string marker)
    {
        XInputShimInstaller installer = NewInstaller();
        string game = Game("ac-" + marker.Replace('.', '_'), 0x8664, marker);
        XInputShimResult result = installer.Install(game);
        Assert.False(result.Ok);
        Assert.Contains("anti-cheat", result.Message);
        Assert.All(XInputShimInstaller.ShimNames, n => Assert.False(File.Exists(Path.Combine(game, n))));
    }

    [Fact]
    public void An_existing_xinput_file_from_another_tool_is_never_replaced()
    {
        XInputShimInstaller installer = NewInstaller();
        string game = Game("x360ce", 0x8664, "xinput1_3.dll");
        byte[] before = File.ReadAllBytes(Path.Combine(game, "xinput1_3.dll"));

        XInputShimResult result = installer.Install(game);

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(game, "xinput1_3.dll")));
        Assert.False(File.Exists(Path.Combine(game, "xinput1_4.dll")));   // nothing half-installed
    }

    [Fact]
    public void A_file_that_changed_since_install_is_left_alone_on_removal()
    {
        XInputShimInstaller installer = NewInstaller();
        string game = Game("modified", 0x8664);
        installer.Install(game);
        File.WriteAllText(Path.Combine(game, "xinput1_3.dll"), "someone replaced me");

        XInputShimResult removed = installer.Uninstall(game);

        Assert.True(removed.Ok);
        Assert.Contains("xinput1_3.dll", removed.Message);
        Assert.Equal("someone replaced me", File.ReadAllText(Path.Combine(game, "xinput1_3.dll")));
        Assert.False(File.Exists(Path.Combine(game, "xinput1_4.dll")));
    }

    [Fact]
    public void Uninstall_touches_nothing_for_a_folder_PaRuX_never_installed_into()
    {
        XInputShimInstaller installer = NewInstaller();
        string game = Game("untouched", 0x8664, "xinput1_4.dll");
        XInputShimResult result = installer.Uninstall(game);
        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(game, "xinput1_4.dll")));
    }

    [Fact]
    public void Installing_twice_and_unsafe_folders_are_refused()
    {
        XInputShimInstaller installer = NewInstaller();
        string game = Game("twice", 0x8664);
        Assert.True(installer.Install(game).Ok);
        Assert.False(installer.Install(game).Ok);

        Assert.False(installer.Install(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Ok);
        Assert.False(installer.Install(@"C:\").Ok);
        Assert.False(installer.Install(@"\\server\share\game").Ok);
        Assert.False(installer.Install(@"relative\game").Ok);
        Assert.False(installer.Install(Path.Combine(_root, "does-not-exist")).Ok);
        Assert.False(installer.Install(null).Ok);
    }

    [Fact]
    public void A_folder_without_a_readable_program_is_refused_because_the_architecture_is_unknown()
    {
        XInputShimInstaller installer = NewInstaller();
        string dir = Path.Combine(_root, "games", "noexe");
        Directory.CreateDirectory(dir);
        XInputShimResult result = installer.Install(dir);
        Assert.False(result.Ok);
        Assert.Contains("32-bit or 64-bit", result.Message);
    }
}
