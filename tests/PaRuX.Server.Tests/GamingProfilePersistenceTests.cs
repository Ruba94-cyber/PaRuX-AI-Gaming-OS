using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingProfilePersistenceTests
{
    [Fact]
    public void Corrupt_file_is_preserved_and_edits_are_rejected_without_silent_reset()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "profiles.json");
            const string original = "{broken gaming profile json";
            File.WriteAllText(path, original);
            var store = new GamingProfileStore(path);

            Assert.NotNull(store.LoadError);
            Assert.Throws<InvalidDataException>(() => store.Save(new GamingProfileDto { Name = "No silent overwrite" }));
            Assert.Throws<InvalidDataException>(() => store.Delete("preset-local-console"));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, ".gaming-profiles-*.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Persisted_profiles_survive_reload_and_saved_edits_are_atomic()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "profiles.json");
            var store = new GamingProfileStore(path);
            Assert.Null(store.LoadError);
            var game = store.Save(new GamingProfileDto
            {
                Name = "Favorite user session",
                TriggerProvider = "manual",
                LauncherProvider = "none",
                Enabled = false
            });
            Assert.False(string.IsNullOrWhiteSpace(game.Id));

            var reloaded = new GamingProfileStore(path);
            Assert.Null(reloaded.LoadError);
            Assert.Equal("Favorite user session", reloaded.Get(game.Id)!.Name);
            Assert.True(reloaded.Delete(game.Id));
            Assert.Null(new GamingProfileStore(path).Get(game.Id));
            Assert.Empty(Directory.GetFiles(dir, ".gaming-profiles-*.tmp"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_core_instances_merge_unrelated_changes_instead_of_overwriting_stale_cache()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "profiles.json");
            var first = new GamingProfileStore(path);
            var second = new GamingProfileStore(path);
            var a = first.Save(new GamingProfileDto { Name = "First game" });
            var b = second.Save(new GamingProfileDto { Name = "Second game" });

            var reloaded = new GamingProfileStore(path);
            Assert.NotNull(reloaded.Get(a.Id));
            Assert.NotNull(reloaded.Get(b.Id));
            Assert.Equal(4, reloaded.List().Count); // both original presets plus both additions

            Assert.True(first.Delete(a.Id));
            var c = second.Save(new GamingProfileDto { Name = "Third game" });
            reloaded = new GamingProfileStore(path);
            Assert.Null(reloaded.Get(a.Id));
            Assert.NotNull(reloaded.Get(b.Id));
            Assert.NotNull(reloaded.Get(c.Id));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Corrupt_file_appearing_between_load_and_save_is_never_replaced()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "profiles.json");
            var store = new GamingProfileStore(path);
            const string damaged = "{file damaged by an external writer";
            File.WriteAllText(path, damaged);
            Assert.ThrowsAny<Exception>(() => store.Save(new GamingProfileDto { Name = "Blocked" }));
            Assert.Equal(damaged, File.ReadAllText(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Invalid_or_duplicate_profile_names_do_not_change_the_saved_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "profiles.json");
            var store = new GamingProfileStore(path);
            string original = File.ReadAllText(path);
            Assert.Throws<ArgumentException>(() => store.Save(new GamingProfileDto { Name = " " }));
            Assert.Throws<InvalidOperationException>(() => store.Save(new GamingProfileDto { Name = "Local Console" }));
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Explicitly_empty_profile_list_is_not_overwritten_by_default_presets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "profiles.json");
            File.WriteAllText(path, "[]");
            var store = new GamingProfileStore(path);
            Assert.Null(store.LoadError);
            Assert.Empty(store.List());
            Assert.Equal("[]", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public sealed class GamingProfilePersistenceHttpTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public GamingProfilePersistenceHttpTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Corrupt_store_returns_conflict_and_preserves_original_for_authenticated_client()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "profiles.json");
            const string corrupt = "{cannot parse";
            File.WriteAllText(file, corrupt);
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<GamingProfileStore>();
                services.AddSingleton(new GamingProfileStore(file));
            }));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-PaRuX-Token", factory.Services.GetRequiredService<ServerState>().Token);
            using var reply = await client.GetAsync("/api/v1/gaming/profiles");
            Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
            Assert.Equal(corrupt, File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
