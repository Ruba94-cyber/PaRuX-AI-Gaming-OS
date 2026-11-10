using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The data root is where conversations, memory, the RAG index, settings, paired-device
/// credentials, downloaded models and installed runtimes live, so every component has to agree on
/// it. These assert the naming rule without touching the real %LOCALAPPDATA%, and guard the test
/// suite's own isolation from the developer's installation.
/// </summary>
public sealed class PaRuXDataRootTests
{
    [Fact]
    public void The_production_location_is_PaRuX_under_LocalApplicationData()
    {
        // Asserted without reading PaRuXDataRoot.Path, which the suite has already redirected.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(localAppData, "PaRuX"), PaRuXDataRoot.DefaultLocation());
    }

    [Fact]
    public void Corporate_portable_data_is_resolved_for_desktop_and_nested_server()
    {
        string root = Path.Combine(Path.GetTempPath(), "corp-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string nestedServer = Path.Combine(root, "PaRuX.Server");
            Directory.CreateDirectory(nestedServer);
            Assert.Null(PaRuXDataRoot.FindCorporatePortableDataRoot(root));
            File.WriteAllText(Path.Combine(root, CorporateMode.MarkerFileName), "Corporate");
            Assert.Null(PaRuXDataRoot.FindCorporatePortableDataRoot(root));
            string data = Path.Combine(root, "PaRuXData");
            Directory.CreateDirectory(data);
            Assert.Equal(data, PaRuXDataRoot.FindCorporatePortableDataRoot(root));
            Assert.Equal(data, PaRuXDataRoot.FindCorporatePortableDataRoot(nestedServer));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Standard_portable_direct_exe_and_nested_Core_resolve_the_same_movable_data_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-standard-portable-" + Guid.NewGuid().ToString("N"));
        string moved = root + "-moved";
        try
        {
            Directory.CreateDirectory(root);
            string core = Path.Combine(root, "PaRuX.Server");
            Directory.CreateDirectory(core);
            Assert.Null(PaRuXDataRoot.FindPortableDataRoot(root));

            // A marker without the actual data folder does not make an ordinary MSI portable.
            File.WriteAllText(Path.Combine(root, PaRuXDataRoot.PortableMarkerFileName), "Standard ZIP");
            Assert.Null(PaRuXDataRoot.FindPortableDataRoot(root));
            string data = Path.Combine(root, "PaRuXData");
            Directory.CreateDirectory(data);
            Assert.Equal(data, PaRuXDataRoot.FindPortableDataRoot(root));
            Assert.Equal(data, PaRuXDataRoot.FindPortableDataRoot(core));
            Assert.Equal(data, PaRuXDataRoot.ResolveDefaultLocation(root,
                Path.Combine(Path.GetTempPath(), "unexpected-host-profile")));
            Assert.Null(PaRuXDataRoot.FindCorporatePortableDataRoot(root));

            // Relocation across drive letters/USB paths must not retain the old root.
            Directory.Move(root, moved);
            string movedData = Path.Combine(moved, "PaRuXData");
            Assert.Equal(movedData, PaRuXDataRoot.FindPortableDataRoot(moved));
            Assert.Equal(movedData,
                PaRuXDataRoot.FindPortableDataRoot(Path.Combine(moved, "PaRuX.Server")));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            try { if (Directory.Exists(moved)) Directory.Delete(moved, true); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Corporate_marker_also_resolves_new_shared_portable_data_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-corp-portable-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "PaRuXData"));
            File.WriteAllText(Path.Combine(root, CorporateMode.MarkerFileName), "Corporate ZIP");
            Assert.Equal(Path.Combine(root, "PaRuXData"), PaRuXDataRoot.FindPortableDataRoot(root));
            Assert.Equal(Path.Combine(root, "PaRuXData"),
                PaRuXDataRoot.FindPortableDataRoot(Path.Combine(root, "PaRuX.Server")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void The_test_run_is_isolated_from_the_real_data_root()
    {
        // If this ever fails, the suite is writing into the developer's installation again.
        string resolved = PaRuXDataRoot.Path;

        Assert.NotEqual(PaRuXDataRoot.DefaultLocation(), resolved);
        Assert.Equal(Path.Combine(resolved, "server", "x.db"), PaRuXDataRoot.Combine("server", "x.db"));
    }
}
