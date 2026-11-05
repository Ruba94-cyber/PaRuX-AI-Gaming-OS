using System.Collections.Concurrent;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

public sealed record KnowledgeJobStatus(string SourceId, string State, IndexProgress? Progress, string? Error, DateTime UpdatedUtc);

/// <summary>Lets the context engine ask the knowledge index without a constructor change (set once at start-up).</summary>
public static class KnowledgeContextProvider
{
    public static KnowledgeJobService? Current { get; set; }

    /// <summary>Position-bearing hits for chat, or null when nothing is indexed/found (the caller falls back to the classic store).</summary>
    public static IReadOnlyList<KnowledgeHit>? TrySearch(string query, int topK, IReadOnlyCollection<string>? sourceIds = null)
    {
        try
        {
            KnowledgeJobService? service = Current;
            if (service == null || !service.HasData) return null;
            IReadOnlyList<KnowledgeHit> hits = service.Index.Search(query, KnowledgeSearchMode.Auto, topK, service.ScopedSourceIds(sourceIds));
            return hits.Count == 0 ? null : hits;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Runs index jobs for the folders the person registered in the existing RAG source list (no second folder list). One job per
/// source, pausable and cancellable, at below-normal priority with an optional throttle so small PCs stay responsive.
/// Reads documents only; the index lives in the user's data root.
/// </summary>
public sealed class KnowledgeJobService
{
    private readonly RagStore _sources;
    private readonly KnowledgeSettingsStore _settings;
    private readonly EmbeddingService? _embeddings;
    private readonly IOcrEngine _ocr;
    private readonly IdleModelUnloader? _unloader;
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, IndexControl Control, Task Task)> _jobs = new();
    private readonly ConcurrentDictionary<string, KnowledgeJobStatus> _status = new();

    public KnowledgeIndex Index { get; }
    public KnowledgeSettingsStore Settings => _settings;

    public KnowledgeJobService(RagStore sources, KnowledgeSettingsStore? settings = null, EmbeddingService? embeddings = null,
        KnowledgeIndex? index = null, IOcrEngine? ocr = null, IRetrievalEmbeddingProvider? embedder = null)
    {
        _sources = sources;
        _settings = settings ?? KnowledgeSettingsStore.Shared;
        _embeddings = embeddings;
        _ocr = ocr ?? new WindowsOcrEngine();
        Index = index ?? CreateIndex(PaRuXDataRoot.Combine("knowledge", "knowledge.db"), embedder, BuildOptions());
        if (embeddings != null)
            _unloader = new IdleModelUnloader(embeddings, () => CurrentPlan().IdleUnload);
    }

    /// <summary>The resource plan the current settings and this PC resolve to.</summary>
    public KnowledgeResourcePlan CurrentPlan() =>
        KnowledgeResourcePlan.Resolve(_settings.Get(), KnowledgeResourcePlan.DetectTotalRam(), KnowledgeResourcePlan.DetectDedicatedGpu(), Environment.ProcessorCount);

    /// <summary>Index options for the current settings: profile limits, thread cap and OCR only when the person opted in.</summary>
    public KnowledgeIndexOptions BuildOptions()
    {
        KnowledgeSettings settings = _settings.Get();
        KnowledgeResourcePlan plan = CurrentPlan();
        if (_embeddings != null) _embeddings.ThreadLimit = plan.EmbeddingThreads;
        return new KnowledgeIndexOptions
        {
            Plan = plan,
            EmbedBatchSize = plan.EmbedBatchSize,
            Limits = ExtractionLimits.Default with { Ocr = settings.OcrEnabled ? _ocr : null, OcrLanguage = settings.OcrLanguage }
        };
    }

    /// <summary>The default index. The embedder is what makes Semantic/Hybrid search work: without it the index is lexical-only
    /// (found in the Corporate Lite measurement run: 0 semantic hits for 50 queries).</summary>
    public static KnowledgeIndex CreateIndex(string dbPath, IRetrievalEmbeddingProvider? embedder, KnowledgeIndexOptions? options = null) =>
        new(dbPath, options ?? new KnowledgeIndexOptions { ThrottleMilliseconds = 5 }, embedder);

    public bool HasData
    {
        get { try { return _sources.ListSources().Any(s => Index.Stats(s.Id).chunks > 0); } catch (Exception) { return false; } }
    }

    public IReadOnlyCollection<string> EnabledSourceIds() => _sources.ListSources().Where(s => s.Enabled).Select(s => s.Id).ToList();

    /// <summary>The chosen sources that exist and are enabled; every enabled source when none are chosen.</summary>
    public IReadOnlyCollection<string> ScopedSourceIds(IReadOnlyCollection<string>? chosen)
    {
        IReadOnlyCollection<string> enabled = EnabledSourceIds();
        if (chosen == null || chosen.Count == 0) return enabled;
        return enabled.Where(id => chosen.Contains(id)).ToList();
    }

    public bool Start(string sourceId)
    {
        RagSourceDto? source = _sources.ListSources().Find(s => s.Id == sourceId);
        if (source == null) return false;
        if (SensitivePathPolicy.EvaluateFolder(source.FolderPath).Verdict == SensitiveVerdict.Blocked)
        {
            _status[sourceId] = new KnowledgeJobStatus(sourceId, "failed", null, "blocked_sensitive_path", DateTime.UtcNow);
            return false;   // a folder added before the policy existed is still refused
        }
        if (_jobs.TryGetValue(sourceId, out var running) && !running.Task.IsCompleted) return true;
        Index.UpdateOptions(BuildOptions());
        var cts = new CancellationTokenSource();
        var control = new IndexControl();
        var progress = new Progress<IndexProgress>(p => _status[sourceId] = new KnowledgeJobStatus(sourceId, p.State, p, null, DateTime.UtcNow));
        Task task = Task.Run(async () =>
        {
            try
            {
                IndexProgress final = await Index.IndexFolderAsync(sourceId, source.FolderPath, progress, control, cts.Token).ConfigureAwait(false);
                _status[sourceId] = new KnowledgeJobStatus(sourceId, "done", final, null, DateTime.UtcNow);
            }
            catch (OperationCanceledException) { _status[sourceId] = new KnowledgeJobStatus(sourceId, "cancelled", null, null, DateTime.UtcNow); }
            catch (Exception ex) { _status[sourceId] = new KnowledgeJobStatus(sourceId, "failed", null, ex.GetType().Name, DateTime.UtcNow); }
        });
        _jobs[sourceId] = (cts, control, task);
        _status[sourceId] = new KnowledgeJobStatus(sourceId, "running", null, null, DateTime.UtcNow);
        return true;
    }

    public KnowledgeJobStatus? Status(string sourceId) => _status.TryGetValue(sourceId, out var s) ? s : null;
    public OcrAvailability OcrAvailability() => _ocr.CheckAvailability();

    public bool Pause(string id) { if (!_jobs.TryGetValue(id, out var j)) return false; j.Control.Pause(); return true; }
    public bool Resume(string id) { if (!_jobs.TryGetValue(id, out var j)) return false; j.Control.Resume(); return true; }
    public bool Cancel(string id) { if (!_jobs.TryGetValue(id, out var j)) return false; j.Cts.Cancel(); return true; }

    public void Remove(string sourceId)
    {
        Cancel(sourceId);
        Index.RemoveSource(sourceId);
        _status.TryRemove(sourceId, out _);
    }
}

public sealed class KnowledgeSettingsUpdate
{
    public bool? OcrEnabled { get; set; }
    public string? OcrLanguage { get; set; }
    public bool? StrictOffline { get; set; }
    public string? Profile { get; set; }
}

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/knowledge/search", (string q, string? mode, int? k, KnowledgeJobService svc) =>
        {
            var m = Enum.TryParse(mode, true, out KnowledgeSearchMode parsed) ? parsed : KnowledgeSearchMode.Auto;
            IReadOnlyList<KnowledgeHit> hits = svc.Index.Search(q ?? string.Empty, m, Math.Clamp(k ?? 10, 1, 50), svc.EnabledSourceIds());
            return Results.Ok(hits.Select(h => new
            {
                h.FileName, h.FilePath, citation = h.Citation, text = h.Text, match = h.Match, score = h.Score,
                page = h.Position.Page, sheet = h.Position.Sheet, cellRange = h.Position.CellRange, slide = h.Position.Slide, heading = h.Position.Heading
            }));
        });
        app.MapPost("/api/v1/knowledge/sources/{id}/index", (string id, KnowledgeJobService svc) =>
        {
            if (svc.Start(id)) return Results.Accepted();
            return svc.Status(id)?.Error == "blocked_sensitive_path"
                ? Results.Json(new { error = "This folder holds credentials, keys or private application data and is not indexed.", code = "sensitive_blocked" }, statusCode: 403)
                : Results.NotFound(new { error = "RAG source not found" });
        }).RequireDesktopOwner();
        app.MapGet("/api/v1/knowledge/settings", (bool? probe, KnowledgeJobService svc) =>
        {
            KnowledgeSettings s = svc.Settings.Get();
            KnowledgeResourcePlan plan = svc.CurrentPlan();
            long ram = KnowledgeResourcePlan.DetectTotalRam();
            bool gpu = KnowledgeResourcePlan.DetectDedicatedGpu();
            OcrAvailability? ocr = probe == true ? svc.OcrAvailability() : null;
            return Results.Ok(new
            {
                ocrEnabled = s.OcrEnabled, ocrLanguage = s.OcrLanguage, strictOffline = s.StrictOffline || CorporateMode.IsActive,
                corporateOffline = CorporateMode.IsActive,
                profile = (s.Profile ?? plan.Profile).ToString(), profileIsAutomatic = s.Profile == null,
                recommendedProfile = KnowledgeResourcePlan.Recommend(ram, gpu).ToString(),
                embeddingThreads = plan.EmbeddingThreads, dutyCycle = plan.DutyCycle, memoryBudgetMb = plan.ManagedMemoryBudgetBytes >> 20,
                idleUnloadSeconds = (int)plan.IdleUnload.TotalSeconds, totalRamMb = ram >> 20, dedicatedGpu = gpu,
                ocr = ocr == null ? null : new { available = ocr.Available, reason = ocr.Reason, languages = ocr.Languages }
            });
        });
        app.MapPut("/api/v1/knowledge/settings", (KnowledgeSettingsUpdate update, KnowledgeJobService svc) =>
        {
            KnowledgeSettings s = svc.Settings.Get();
            if (update.OcrEnabled.HasValue) s.OcrEnabled = update.OcrEnabled.Value;
            if (update.OcrLanguage != null) s.OcrLanguage = update.OcrLanguage.Length == 0 ? null : update.OcrLanguage;
            if (update.StrictOffline.HasValue) s.StrictOffline = update.StrictOffline.Value;
            if (update.Profile != null)
            {
                if (update.Profile.Equals("auto", StringComparison.OrdinalIgnoreCase)) s.Profile = null;
                else if (Enum.TryParse(update.Profile, true, out KnowledgeProfile parsed)) s.Profile = parsed;
                else return Results.BadRequest(new { error = "profile must be auto, Eco, Balanced or Performance." });
            }
            svc.Settings.Save(s);
            svc.Index.UpdateOptions(svc.BuildOptions());
            return Results.NoContent();
        }).RequireDesktopOwner();
        app.MapPost("/api/v1/knowledge/sources/{id}/pause", (string id, KnowledgeJobService svc) => svc.Pause(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapPost("/api/v1/knowledge/sources/{id}/resume", (string id, KnowledgeJobService svc) => svc.Resume(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapPost("/api/v1/knowledge/sources/{id}/cancel", (string id, KnowledgeJobService svc) => svc.Cancel(id) ? Results.NoContent() : Results.NotFound()).RequireDesktopOwner();
        app.MapGet("/api/v1/knowledge/sources/{id}/status", (string id, KnowledgeJobService svc) =>
        {
            var (files, chunks) = svc.Index.Stats(id);
            return Results.Ok(new { job = svc.Status(id), files, chunks, problems = svc.Index.Problems(id) });
        });
        app.MapDelete("/api/v1/knowledge/sources/{id}", (string id, KnowledgeJobService svc) => { svc.Remove(id); return Results.NoContent(); }).RequireDesktopOwner();
    }
}
