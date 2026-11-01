using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingLocalFileSafetyPolicyTests
{
    [Fact]
    public void Installer_and_executable_use_one_local_file_boundary()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-file-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "package.zip");
            string helper = Path.Combine(root, "helper.exe");
            File.WriteAllText(archive, "fixture: never execute");
            File.WriteAllText(helper, "fixture: never execute");
            Assert.True(GameInstallerInspectionService.ValidLocalPackagePath(archive));
            Assert.False(CustomExecutableInputProvider.IsSafeHelperPath(archive));
            Assert.True(CustomExecutableInputProvider.IsSafeHelperPath(helper));
            Assert.True(GameInstallerInspectionService.ValidLocalPackagePath(helper));
            Assert.False(GameInstallerInspectionService.ValidLocalPackagePath(archive + " --run"));
            Assert.False(GameInstallerInspectionService.ValidLocalPackagePath(Path.Combine(root, "..", Path.GetFileName(root), "package.zip")));
            Assert.False(GameInstallerInspectionService.ValidLocalPackagePath(@"\\server\share\package.zip"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Linked_directory_and_leaf_are_rejected_if_symlinks_are_supported()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-linked-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string real = Path.Combine(root, "real");
            Directory.CreateDirectory(real);
            string package = Path.Combine(real, "package.zip");
            File.WriteAllText(package, "fixture");
            string linkedDirectory = Path.Combine(root, "alias");
            string linkedFile = Path.Combine(root, "file.zip");
            try
            {
                Directory.CreateSymbolicLink(linkedDirectory, real);
                File.CreateSymbolicLink(linkedFile, package);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Some Windows hosts prohibit creating symlinks without Developer Mode.
                // The physical gate must record this case as not exercised.
                return;
            }
            Assert.False(GameInstallerInspectionService.ValidLocalPackagePath(Path.Combine(linkedDirectory, "package.zip")));
            Assert.False(GameInstallerInspectionService.ValidLocalPackagePath(linkedFile));
            Assert.True(GameInstallerInspectionService.ValidLocalPackagePath(package));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
