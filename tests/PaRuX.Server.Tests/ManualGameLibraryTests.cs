using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Server;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManualGameLibraryTests
{
    [Fact]
    public void Default_path_uses_the_process_data_root() =>
        Assert.Equal(Path.Combine(PaRuXDataRoot.Path, "server", "manual-games.json"), ManualGameLibrary.DefaultPath);

    [Fact]
    public void Write_failure_does_not_report_success_or_leave_temporary_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-games-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "library.json");
            Directory.CreateDirectory(target);
            var store = new ManualGameLibrary(target);
            Exception error = Assert.ThrowsAny<Exception>(() => store.Add(new("Game", Path.Combine(dir, "game.exe"))));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(Directory.Exists(target));
            Assert.Empty(Directory.GetFiles(dir, ".manual-games-*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void Add_reload_duplicate_and_remove_are_persistent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-games-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(dir, "library.json");
            string exe = Path.Combine(dir, "game.exe");
            var store = new ManualGameLibrary(file);
            ManualGameDto added = store.Add(new(" Game ", exe));
            Assert.Equal("Game", added.Name);
            Assert.Equal(added, Assert.Single(new ManualGameLibrary(file).List()));
            Assert.Throws<InvalidOperationException>(() => store.Add(new("Duplicate", exe)));
            Assert.True(store.Remove(added.Id));
            Assert.False(store.Remove(added.Id));
            Assert.Empty(new ManualGameLibrary(file).List());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Alternate_working_folders_are_distinct_launch_profiles_and_omitted_folder_is_not()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-profile-variants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string program = Path.Combine(root, "game.exe");
            string alternative = Path.Combine(root, "alternate");
            Directory.CreateDirectory(alternative);
            var library = new ManualGameLibrary(Path.Combine(root, "library.json"));
            library.Add(new("Home", program));
            // Explicitly writing the implied executable directory must not create a duplicate.
            Assert.Throws<InvalidOperationException>(() =>
                library.Add(new("Same", program, WorkingDirectory: root)));
            library.Add(new("Portable alternate", program, WorkingDirectory: alternative));
            library.Add(new("Application shortcut", program, Kind: ManualEntryKinds.Application));
            var entries = new ManualGameLibrary(Path.Combine(root, "library.json")).List();
            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, e => e.WorkingDirectory == alternative);
            Assert.Contains(entries, e => e.Kind == ManualEntryKinds.Application);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_paths_are_rejected_without_creating_a_library()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-games-" + Guid.NewGuid().ToString("N"));
        var store = new ManualGameLibrary(Path.Combine(dir, "library.json"));
        Assert.Throws<ArgumentException>(() => store.Add(new("Game", "relative.exe")));
        Assert.Throws<ArgumentException>(() => store.Add(new("Game", @"\\server\share\game.exe")));
        Assert.Throws<ArgumentException>(() => store.Add(new("Game", Path.Combine(dir, "game.cmd"))));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Independent_store_instances_do_not_lose_concurrent_additions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-games-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(dir, "library.json");
            await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            {
                var store = new ManualGameLibrary(file);
                store.Add(new AddManualGameRequest("Game " + i, Path.Combine(dir, "game" + i + ".exe")));
            })));
            var final = new ManualGameLibrary(file).List();
            Assert.Equal(24, final.Count);
            Assert.Equal(24, final.Select(g => g.Id).Distinct().Count());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Different_store_instances_cannot_insert_the_same_executable_twice()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-games-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(dir, "library.json");
            string executable = Path.Combine(dir, "game.exe");
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            {
                try
                {
                    new ManualGameLibrary(file).Add(new AddManualGameRequest("Game " + i, executable));
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            })));
            Assert.Single(outcomes.Where(success => success));
            Assert.Single(new ManualGameLibrary(file).List());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Corrupt_or_future_library_is_preserved_and_never_replaced()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-games-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "library.json");
            var store = new ManualGameLibrary(file);
            foreach (string contents in new[] { "{ broken", "{\"Version\":2,\"Games\":[]}" })
            {
                File.WriteAllText(file, contents);
                Assert.ThrowsAny<Exception>(() => store.Add(new("Game", Path.Combine(dir, "game.exe"))));
                Assert.Equal(contents, File.ReadAllText(file));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}

public sealed class ManualGameLibraryHttpTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private sealed class UnaddressedFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public ManualGameLibraryHttpTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Library_routes_reject_missing_credentials()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/gaming/library")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/gaming/library", new AddManualGameRequest("x", "x"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/v1/gaming/library/x")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_without_desktop_address_is_forbidden()
    {
        using var factory = new UnaddressedFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/gaming/library")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/gaming/library", new AddManualGameRequest("x", "x"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/v1/gaming/library/x")).StatusCode);
    }

    [Fact]
    public async Task Future_version_returns_errors_without_replacing_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-games-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "library.json");
        const string contents = "{\"Version\":2,\"Games\":[]}";
        File.WriteAllText(path, contents);
        try
        {
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ManualGameLibrary>();
                services.AddSingleton(new ManualGameLibrary(path));
            }));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, (await client.GetAsync("/api/v1/gaming/library")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, (await client.PostAsJsonAsync("/api/v1/gaming/library", new AddManualGameRequest("Game", Path.Combine(dir, "game.exe")))).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, (await client.DeleteAsync("/api/v1/gaming/library/x")).StatusCode);
            Assert.Equal(contents, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
