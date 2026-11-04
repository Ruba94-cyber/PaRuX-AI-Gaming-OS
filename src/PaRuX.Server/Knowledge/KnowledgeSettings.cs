using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

/// <summary>How hard indexing may work. "Eco" is the default on small PCs.</summary>
public enum KnowledgeProfile { Eco, Balanced, Performance }

/// <summary>
/// Persisted knowledge settings. Every capability that reads more than plain text (OCR) or touches the network (model
/// download) is off by default and changed only by the person.
/// </summary>
public sealed class KnowledgeSettings
{
    /// <summary>Opt-in: recognise text in images and scanned PDF pages with the OCR engine.</summary>
    public bool OcrEnabled { get; set; }
    /// <summary>BCP-47 tag, or null to use the Windows user-profile languages.</summary>
    public string? OcrLanguage { get; set; }
    /// <summary>Null = choose automatically from this PC (see <see cref="KnowledgeResourcePlan.Recommend"/>).</summary>
    public KnowledgeProfile? Profile { get; set; }
    /// <summary>Blocks every model download and update check this feature could make. Off by default.</summary>
    public bool StrictOffline { get; set; }
}

public sealed class KnowledgeSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly object _gate = new();
    private KnowledgeSettings? _cache;

    public KnowledgeSettingsStore(string? path = null) => _path = path ?? PaRuXDataRoot.Combine("knowledge", "settings.json");

    /// <summary>The store for the real data root; used by code that has no dependency injection (model installers).</summary>
    public static KnowledgeSettingsStore Shared { get; } = new();

    public KnowledgeSettings Get()
    {
        lock (_gate)
        {
            if (_cache != null) return Clone(_cache);
            KnowledgeSettings s = new();
            try { if (File.Exists(_path)) s = JsonSerializer.Deserialize<KnowledgeSettings>(File.ReadAllText(_path), Json) ?? new(); }
            catch (Exception) { s = new KnowledgeSettings(); }   // a corrupt file falls back to the safe defaults
            _cache = s;
            return Clone(s);
        }
    }

    public void Save(KnowledgeSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, _path, true);
            _cache = Clone(settings);
        }
    }

    private static KnowledgeSettings Clone(KnowledgeSettings s) =>
        new() { OcrEnabled = s.OcrEnabled, OcrLanguage = s.OcrLanguage, Profile = s.Profile, StrictOffline = s.StrictOffline };
}

/// <summary>The single answer to "may this feature touch the network?". Corporate Lite is always offline.</summary>
public static class OfflinePolicy
{
    public static bool IsBlocked(KnowledgeSettingsStore? store = null) =>
        CorporateMode.IsActive || (store ?? KnowledgeSettingsStore.Shared).Get().StrictOffline;

    /// <summary>Throws <see cref="OfflineBlockedException"/> before any request is built.</summary>
    public static void ThrowIfBlocked(string what, KnowledgeSettingsStore? store = null)
    {
        if (IsBlocked(store))
            throw new OfflineBlockedException("Strict offline mode is on: " + what + " is blocked. Nothing was downloaded.");
    }
}

public sealed class OfflineBlockedException : InvalidOperationException
{
    public OfflineBlockedException(string message) : base(message) { }
}

/// <summary>
/// Resource limits for indexing, derived from a profile and this PC. The Light/Eco plan targets a 16 GB CPU-only laptop:
/// bounded managed memory, a thread cap for embeddings, a duty cycle so the PC stays responsive, below-normal priority,
/// and the embedding model unloaded when idle.
/// </summary>
public sealed record KnowledgeResourcePlan(
    KnowledgeProfile Profile,
    int EmbeddingThreads,
    /// <summary>Share of wall time indexing may keep a core busy (0.25 = it sleeps 3x as long as it works).</summary>
    double DutyCycle,
    int ThrottleMilliseconds,
    ThreadPriority WorkerPriority,
    long MaxFileBytes,
    long MaxCharacters,
    int EmbedBatchSize,
    long ManagedMemoryBudgetBytes,
    TimeSpan IdleUnload)
{
    public static KnowledgeResourcePlan For(KnowledgeProfile profile, int logicalCores)
    {
        int cores = Math.Max(1, logicalCores);
        return profile switch
        {
            KnowledgeProfile.Eco => new(profile, Math.Max(1, cores / 4), 0.25, 20, ThreadPriority.BelowNormal,
                256L << 20, 5_000_000, 4, 768L << 20, TimeSpan.FromSeconds(60)),
            KnowledgeProfile.Performance => new(profile, Math.Max(1, cores - 1), 1.0, 0, ThreadPriority.Normal,
                2048L << 20, 20_000_000, 16, 3072L << 20, TimeSpan.FromMinutes(10)),
            _ => new(KnowledgeProfile.Balanced, Math.Max(1, cores / 2), 0.5, 5, ThreadPriority.BelowNormal,
                1024L << 20, 10_000_000, 8, 1536L << 20, TimeSpan.FromMinutes(3))
        };
    }

    /// <summary>A PC with 16 GB or less and no dedicated GPU runs the light plan; others balanced. The person can always override.</summary>
    public static KnowledgeProfile Recommend(long totalRamBytes, bool hasDedicatedGpu) =>
        totalRamBytes <= 17L * 1024 * 1024 * 1024 && !hasDedicatedGpu ? KnowledgeProfile.Eco : KnowledgeProfile.Balanced;

    public static KnowledgeResourcePlan Resolve(KnowledgeSettings settings, long totalRamBytes, bool hasDedicatedGpu, int logicalCores) =>
        For(settings.Profile ?? Recommend(totalRamBytes, hasDedicatedGpu), logicalCores);

    /// <summary>Total physical memory as reported by the runtime (GC info), 0 if unknown.</summary>
    public static long DetectTotalRam()
    {
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch (Exception) { return 0; }
    }

    /// <summary>
    /// True when a discrete GPU adapter is visible. Conservative: unknown counts as "no dedicated GPU" only when no adapter
    /// list is readable, so a gaming PC is never mistaken for a light PC by an enumeration failure.
    /// </summary>
    public static bool DetectDedicatedGpu()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (key == null) return true;   // unreadable: do not claim a light PC
            foreach (string name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                string desc = (sub?.GetValue("DriverDesc") as string) ?? string.Empty;
                if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
                if (desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && (desc.Contains("RX", StringComparison.OrdinalIgnoreCase) || desc.Contains("Pro W", StringComparison.OrdinalIgnoreCase))) return true;
                if (desc.Contains("Arc", StringComparison.OrdinalIgnoreCase) && desc.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch (Exception) { return true; }
    }
}

/// <summary>Runs CPU-heavy work on a dedicated thread at a chosen priority (the thread pool cannot be re-prioritised).</summary>
public static class LowPriorityRunner
{
    public static Task<T> Run<T>(Func<T> work, ThreadPriority priority, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ct.ThrowIfCancellationRequested(); tcs.SetResult(work()); }
            catch (OperationCanceledException) { tcs.SetCanceled(ct); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Priority = priority, Name = "parux-knowledge-extract" };
        thread.Start();
        return tcs.Task;
    }
}

/// <summary>Keeps managed memory under the plan's budget between files: collect once, then wait briefly instead of growing.</summary>
public sealed class MemoryBudgetGuard
{
    private readonly long _budget;
    private readonly Func<long> _used;
    public MemoryBudgetGuard(long budgetBytes, Func<long>? used = null) { _budget = budgetBytes; _used = used ?? (() => GC.GetTotalMemory(false)); }
    public int Collections { get; private set; }
    public int Waits { get; private set; }

    public async Task WaitForHeadroomAsync(CancellationToken ct, int maxWaits = 20, int waitMs = 100)
    {
        if (_budget <= 0 || _used() <= _budget) return;
        GC.Collect(); GC.WaitForPendingFinalizers(); Collections++;
        for (int i = 0; i < maxWaits && _used() > _budget; i++)
        {
            Waits++;
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
            GC.Collect(); Collections++;
        }
    }
}

/// <summary>A model that can be unloaded to give memory back.</summary>
public interface IUnloadableModel
{
    bool IsLoaded { get; }
    DateTime LastUsedUtc { get; }
    /// <summary>Unloads only when idle for at least <paramref name="idle"/> and not in use. True if it unloaded.</summary>
    bool UnloadIfIdle(TimeSpan idle);
}

/// <summary>Periodically unloads idle models so a 16 GB PC gets the memory back after a search or an index run.</summary>
public sealed class IdleModelUnloader : IDisposable
{
    private readonly IUnloadableModel _model;
    private readonly Func<TimeSpan> _idle;
    private readonly Timer _timer;
    public int Unloads { get; private set; }

    public IdleModelUnloader(IUnloadableModel model, Func<TimeSpan> idle, TimeSpan? period = null)
    {
        _model = model; _idle = idle;
        TimeSpan p = period ?? TimeSpan.FromSeconds(30);
        _timer = new Timer(_ => Tick(), null, p, p);
    }

    public void Tick()
    {
        try { if (_model.IsLoaded && _model.UnloadIfIdle(_idle())) Unloads++; }
        catch (Exception) { /* unloading is best effort */ }
    }

    public void Dispose() => _timer.Dispose();
}
