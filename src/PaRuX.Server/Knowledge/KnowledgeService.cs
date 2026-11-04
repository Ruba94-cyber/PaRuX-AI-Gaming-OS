using System.Collections.Concurrent;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

public sealed record KnowledgeJobStatus(string SourceId, string State, IndexProgress? Progress, string? Error, DateTime UpdatedUtc);

/// <summary>Lets the context engine ask the knowledge index without a constructor change (set once at start-up).</summary>
public static class KnowledgeContextProvider
{
    public static KnowledgeJobService? Current { get; set; }

    /// <summary>Position-bearing hits for chat, or null when nothing is indexed/found (the caller falls back to the classic store).</summary>
    public static IReadOnlyList<KnowledgeHit>? TrySearch(string query, int topK)
    {
        try
        {
            KnowledgeJobService? service = Current;
            if (service == null || !service.HasData) return null;
            IReadOnlyList<KnowledgeHit> hits = service.Index.Search(query, KnowledgeSearchMode.Auto, topK, service.EnabledSourceIds());
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
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, IndexControl Control, Task Task)> _jobs = new();
    private readonly ConcurrentDictionary<string, KnowledgeJobStatus> _status = new();

    public KnowledgeIndex Index { get; }

    public KnowledgeJobService(RagStore sources, KnowledgeIndex? index = null)
    {
        _sources = sources;
        Index = index ?? new KnowledgeIndex(PaRuXDataRoot.Combine("knowledge", "knowledge.db"),
            new KnowledgeIndexOptions { ThrottleMilliseconds = 5 });
    }

    public bool HasData
    {
        get { try { return _sources.ListSources().Any(s => Index.Stats(s.Id).chunks > 0); } catch (Exception) { return false; } }
    }

    public IReadOnlyCollection<string> EnabledSourceIds() => _sources.ListSources().Where(s => s.Enabled).Select(s => s.Id).ToList();

    public bool Start(string sourceId)
    {
        RagSourceDto? source = _sources.ListSources().Find(s => s.Id == sourceId);
        if (source == null) return false;
        if (_jobs.TryGetValue(sourceId, out var running) && !running.Task.IsCompleted) return true;
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
            svc.Start(id) ? Results.Accepted() : Results.NotFound(new { error = "RAG source not found" })).RequireDesktopOwner();
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
