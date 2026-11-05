using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Discover;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Local, owner-managed wishlist. No network calls, timer, notifications or game execution.
/// The store is separate from the installed-game library: a product identifier is metadata,
/// not an executable path, purchase authorization or installer request.
/// </summary>
public sealed class LocalWishlistStore
{
    private sealed record WishlistFile(int Version, List<LocalWishlistEntry> Entries);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private const long MaximumFileBytes = 1024 * 1024;

    public LocalWishlistStore()
        : this(Path.Combine(PaRuXDataRoot.Path, "server", "game-wishlist.json")) { }

    public LocalWishlistStore(string path) => _path = Path.GetFullPath(path);

    public IReadOnlyList<LocalWishlistEntry> List()
    {
        lock (_gate) return Read().ToArray();
    }

    /// <summary>
    /// Add a product with price watch OFF. A caller cannot enable monitoring by
    /// smuggling an opted-in object into this method; SetWatch requires separate consent.
    /// </summary>
    public LocalWishlistEntry Add(LocalWishlistEntry candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.PriceWatchOptIn)
            throw new ArgumentException("Add the item first; price watching requires a separate explicit confirmation.");
        lock (_gate)
        {
            using FileStream lease = AcquireWriteLease();
            List<LocalWishlistEntry> items = Read();
            var result = LocalWishlistPolicy.TryAdd(items, candidate);
            if (result.Error is not null) throw new ArgumentException(result.Error);
            Write(result.Entries);
            return result.Entries[^1];
        }
    }

    /// <summary>Only the explicit owner action can enable watching; this still starts no polling.</summary>
    public LocalWishlistEntry? SetWatch(string productId, bool enabled, bool consent)
    {
        if (!ValidProductId(productId)) throw new ArgumentException("Invalid product id.");
        if (enabled && !consent)
            throw new ArgumentException("Explicit price-watch consent is required.");
        lock (_gate)
        {
            using FileStream lease = AcquireWriteLease();
            List<LocalWishlistEntry> items = Read();
            int index = items.FindIndex(x => x.Store == "steam" && x.ProductId == productId);
            if (index < 0) return null;
            LocalWishlistEntry updated = items[index] with { PriceWatchOptIn = enabled };
            var checkedItem = LocalWishlistPolicy.TryAdd(Array.Empty<LocalWishlistEntry>(), updated);
            if (checkedItem.Error is not null) throw new ArgumentException(checkedItem.Error);
            items[index] = checkedItem.Entries[0];
            Write(items);
            return items[index];
        }
    }

    /// <summary>Remove only the persisted metadata; never changes any game or store account.</summary>
    public bool Remove(string productId)
    {
        if (!ValidProductId(productId)) throw new ArgumentException("Invalid product id.");
        lock (_gate)
        {
            using FileStream lease = AcquireWriteLease();
            List<LocalWishlistEntry> items = Read();
            int removed = items.RemoveAll(x => x.Store == "steam" && x.ProductId == productId);
            if (removed == 0) return false;
            Write(items);
            return true;
        }
    }

    private static bool ValidProductId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 12
        && value.All(c => c is >= '0' and <= '9');

    private void CheckStorePath()
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (!ManualGameLibrary.IsSafeDirectory(directory))
            throw new InvalidDataException("Wishlist directory must not contain links.");
        foreach (string file in new[] { _path, _path + ".lock" })
        {
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked wishlist files are not permitted.");
        }
    }

    /// <summary>Bounded cross-process writer lease, matching the manual-library transaction pattern.</summary>
    private FileStream AcquireWriteLease()
    {
        CheckStorePath();
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(30);
            }
        }
    }

    private List<LocalWishlistEntry> Read()
    {
        CheckStorePath();
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > MaximumFileBytes)
            throw new InvalidDataException("Wishlist file exceeds the local metadata limit.");
        WishlistFile? data;
        try { data = JsonSerializer.Deserialize<WishlistFile>(File.ReadAllText(_path), JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("Wishlist file is invalid; no changes were made.", ex); }
        if (data is null || data.Version != 1 || data.Entries is null
            || data.Entries.Count > LocalWishlistPolicy.MaximumEntries)
            throw new InvalidDataException("Unsupported wishlist file; no changes were made.");
        IReadOnlyList<LocalWishlistEntry> validated = Array.Empty<LocalWishlistEntry>();
        foreach (LocalWishlistEntry? entry in data.Entries)
        {
            if (entry is null) throw new InvalidDataException("Invalid persisted wishlist entry.");
            var result = LocalWishlistPolicy.TryAdd(validated, entry);
            if (result.Error is not null) throw new InvalidDataException("Invalid or duplicated persisted wishlist entry.");
            validated = result.Entries;
        }
        return validated.ToList();
    }

    private void Write(IReadOnlyList<LocalWishlistEntry> entries)
    {
        CheckStorePath();
        string temp = Path.Combine(Path.GetDirectoryName(_path)!, ".wishlist-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new WishlistFile(1, entries.ToList()), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(temp).Length > MaximumFileBytes)
                throw new InvalidDataException("Wishlist file exceeds the local metadata limit.");
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

/// <summary>Explicit, one-shot owner confirmation. No background watcher is installed.</summary>
public sealed record LocalWishlistWatchRequest(bool Enabled, bool Consent);

public static class LocalWishlistEndpoints
{
    public static void MapLocalWishlistEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/gaming/wishlist", (LocalWishlistStore wishlist) =>
        {
            try { return Results.Ok(wishlist.List()) as IResult; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Local wishlist metadata is unavailable."); }
        }).RequireDesktopOwner();

        // Explicit, owner-only, POST-only one-shot quote. No timer or implicit fetch from
        // the list route. Reuses Game Discover's separate opt-in, HTTPS allow-list,
        // rate limiter and Gaming hard-off. Storefront country is *requested*,
        // never verified as the owner's Steam account country.
        app.MapPost("/api/v1/gaming/wishlist/{productId}/quote", async (
            string productId, LocalWishlistQuoteRequest request,
            LocalWishlistStore wishlist, LocalRuntimeService runtime,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Leave Gaming before requesting a store price." }) as IResult;
            if (request is null || !request.Consent)
                return Results.BadRequest(new { error = "One-shot price lookups require explicit confirmation." });
            if (!SteamStoreCatalogProvider.IsValidAppId(productId))
                return Results.BadRequest(new { error = "Invalid Steam app id." });

            LocalWishlistEntry? original;
            try { original = wishlist.List().FirstOrDefault(x => x.Store == "steam" && x.ProductId == productId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Local wishlist metadata is unavailable."); }
            if (original is null) return Results.NotFound(new { error = "Wishlist entry not found." });
            if (original.StoreCountryCode is not { Length: 2 } country
                || !country.All(char.IsAsciiLetter))
                return Results.Conflict(new { error = "Select a two-letter Steam storefront country first." });

            GameDiscoverRuntime discover = GameDiscoverRuntime.For(services);
            string? blocked = discover.Gate.Check();
            if (blocked is not null)
                return Results.Conflict(new { error = GameDiscoverGate.MessageFor(blocked) });

            using var hardOff = new GamingHardOffWatch(() => runtime.GamingMode, ct);
            try
            {
                var result = await discover.Steam.QuotePriceAsync(productId, country, hardOff.Token)
                    .ConfigureAwait(false);
                if (hardOff.Triggered || runtime.GamingMode)
                    return Results.Conflict(new { error = "Store price lookup stopped when Gaming started." });
                blocked = discover.Gate.Check();
                if (blocked is not null)
                    return Results.Conflict(new { error = GameDiscoverGate.MessageFor(blocked) });
                if (!result.IsOk || result.Value is null)
                    return Results.Conflict(new { error = result.Message ?? "The Steam storefront price is unavailable." });

                // A user may change consent, country, currency or remove an entry
                // while the network request is in flight. Recheck current state
                // before using a result; never persist it or send a notification.
                LocalWishlistEntry? current = wishlist.List()
                    .FirstOrDefault(x => x.Store == "steam" && x.ProductId == productId);
                if (current is null || !Equals(current, original))
                    return Results.Conflict(new { error = "Wishlist settings changed during the lookup. Try again." });

                PublicStorePriceSample price = result.Value;
                bool sameCurrency = string.Equals(price.Currency, current.Currency, StringComparison.OrdinalIgnoreCase);
                bool? meetsTarget = sameCurrency && current.PriceTargetMinor is int target
                    ? price.PriceMinor <= target : null;
                return Results.Ok(new LocalWishlistQuoteDto(
                    price.Store, price.ProductId, country.ToUpperInvariant(), price.Currency,
                    price.PriceMinor, current.PriceTargetMinor, meetsTarget, price.ObservedAtUtc,
                    "Indicative Steam storefront quote for the requested country; the Steam account region is not verified. "
                    + "No automatic checks, alerts, purchases or store-account access. "
                    + (sameCurrency ? "" : "The returned currency differs from the saved target currency.")));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return Results.StatusCode(499); }
            catch (OperationCanceledException)
            { return Results.Conflict(new { error = "Store lookup was interrupted; no price was recorded." }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Local wishlist data could not be read."); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/gaming/wishlist", (
            LocalWishlistEntry item, LocalWishlistStore wishlist, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Leave Gaming before editing your wishlist." }) as IResult;
            try { return Results.Ok(wishlist.Add(item)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Local wishlist could not be saved."); }
        }).RequireDesktopOwner();

        app.MapPut("/api/v1/gaming/wishlist/{productId}/watch", (
            string productId, LocalWishlistWatchRequest request, LocalWishlistStore wishlist, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Leave Gaming before changing your wishlist." }) as IResult;
            try
            {
                LocalWishlistEntry? entry = wishlist.SetWatch(productId, request.Enabled, request.Consent);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Local wishlist could not be saved."); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/gaming/wishlist/{productId}", (
            string productId, LocalWishlistStore wishlist, LocalRuntimeService runtime) =>
        {
            if (runtime.GamingMode)
                return Results.Conflict(new { error = "Leave Gaming before changing your wishlist." }) as IResult;
            try { return wishlist.Remove(productId) ? Results.NoContent() : Results.NotFound() as IResult; }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { return Results.Problem("Local wishlist could not be updated."); }
        }).RequireDesktopOwner();
    }
}
