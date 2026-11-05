using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class LocalWishlistStoreTests
{
    private static LocalWishlistEntry Item(string id = "42", int? target = 1200) =>
        new("steam", id, "Synthetic Puzzle", "EUR", target)
        {
            StoreCountryCode = "IT"
        };

    private sealed class Workspace : IDisposable
    {
        public string Folder { get; } =
            Path.Combine(Path.GetTempPath(), "parux-wishlist-tests-" + Guid.NewGuid().ToString("N"));
        public string FileName => Path.Combine(Folder, "wishlist.json");
        public Workspace() => Directory.CreateDirectory(Folder);
        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }

    [Fact]
    public void Add_is_local_and_watch_is_off_even_after_restart()
    {
        using var workspace = new Workspace();
        var first = new LocalWishlistStore(workspace.FileName);
        LocalWishlistEntry saved = first.Add(Item());
        Assert.False(saved.PriceWatchOptIn);
        Assert.False(Assert.Single(new LocalWishlistStore(workspace.FileName).List()).PriceWatchOptIn);
        Assert.Contains("\"Version\": 1", File.ReadAllText(workspace.FileName));
    }

    [Fact]
    public void Adding_opted_in_entry_is_refused_without_saving_anything()
    {
        using var workspace = new Workspace();
        var store = new LocalWishlistStore(workspace.FileName);
        Assert.Throws<ArgumentException>(() => store.Add(Item() with { PriceWatchOptIn = true }));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Watch_requires_explicit_consent_valid_country_and_price_target()
    {
        using var workspace = new Workspace();
        var store = new LocalWishlistStore(workspace.FileName);
        store.Add(Item());
        Assert.Throws<ArgumentException>(() => store.SetWatch("42", true, consent: false));
        Assert.False(Assert.Single(store.List()).PriceWatchOptIn);

        Assert.True(store.SetWatch("42", true, consent: true)!.PriceWatchOptIn);
        Assert.True(Assert.Single(new LocalWishlistStore(workspace.FileName).List()).PriceWatchOptIn);
        Assert.False(store.SetWatch("42", false, consent: false)!.PriceWatchOptIn);
        Assert.False(Assert.Single(new LocalWishlistStore(workspace.FileName).List()).PriceWatchOptIn);

        store.Add(Item("99", target: null));
        Assert.Throws<ArgumentException>(() => store.SetWatch("99", true, consent: true));
        Assert.False(store.List().Single(x => x.ProductId == "99").PriceWatchOptIn);
    }

    [Fact]
    public void Invalid_duplicate_and_missing_products_do_not_mutate_the_file()
    {
        using var workspace = new Workspace();
        var store = new LocalWishlistStore(workspace.FileName);
        store.Add(Item());
        string before = File.ReadAllText(workspace.FileName);
        Assert.Throws<ArgumentException>(() => store.Add(Item()));
        Assert.Throws<ArgumentException>(() => store.Remove("../42"));
        Assert.Throws<ArgumentException>(() => store.SetWatch("../42", true, true));
        Assert.Null(store.SetWatch("999", false, false));
        Assert.False(store.Remove("999"));
        Assert.Equal(before, File.ReadAllText(workspace.FileName));
    }

    [Fact]
    public void Multiple_store_instances_use_the_same_durable_transaction()
    {
        using var workspace = new Workspace();
        var first = new LocalWishlistStore(workspace.FileName);
        var second = new LocalWishlistStore(workspace.FileName);
        first.Add(Item("42"));
        second.Add(Item("99"));
        first.SetWatch("42", true, consent: true);
        var restarted = new LocalWishlistStore(workspace.FileName);
        Assert.Equal(2, restarted.List().Count);
        Assert.True(restarted.List().Single(x => x.ProductId == "42").PriceWatchOptIn);
        Assert.True(second.Remove("42"));
        Assert.Equal("99", Assert.Single(first.List()).ProductId);
    }

    [Fact]
    public void Corrupted_json_and_unsupported_schema_fail_closed_without_overwrite()
    {
        using var workspace = new Workspace();
        var store = new LocalWishlistStore(workspace.FileName);
        File.WriteAllText(workspace.FileName, "{bad json");
        Assert.Throws<InvalidDataException>(() => store.List());
        Assert.Throws<InvalidDataException>(() => store.Add(Item()));
        Assert.Equal("{bad json", File.ReadAllText(workspace.FileName));

        const string unknownSchema = "{\"Version\":2,\"Entries\":[]}";
        File.WriteAllText(workspace.FileName, unknownSchema);
        Assert.Throws<InvalidDataException>(() => store.Remove("42"));
        Assert.Equal(unknownSchema, File.ReadAllText(workspace.FileName));
    }

    [Fact]
    public void Existing_duplicates_and_invalid_opt_in_are_not_silently_loaded()
    {
        using var workspace = new Workspace();
        var store = new LocalWishlistStore(workspace.FileName);
        string duplicate = JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new[] { Item(), Item() }
        });
        File.WriteAllText(workspace.FileName, duplicate);
        Assert.Throws<InvalidDataException>(() => store.List());

        string invalidWatch = JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new[] { Item(target: null) with { PriceWatchOptIn = true } }
        });
        File.WriteAllText(workspace.FileName, invalidWatch);
        Assert.Throws<InvalidDataException>(() => store.List());
    }

    [Fact]
    public void Deleted_entry_does_not_touch_other_file_or_game_data()
    {
        using var workspace = new Workspace();
        var store = new LocalWishlistStore(workspace.FileName);
        string userData = Path.Combine(workspace.Folder, "unrelated.txt");
        File.WriteAllText(userData, "keep");
        store.Add(Item());
        Assert.True(store.Remove("42"));
        Assert.Empty(new LocalWishlistStore(workspace.FileName).List());
        Assert.Equal("keep", File.ReadAllText(userData));
    }
}
