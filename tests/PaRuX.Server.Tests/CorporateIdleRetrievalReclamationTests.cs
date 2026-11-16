using PaRuX.Server;
using PaRuX.Server.Knowledge;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Corporate RAM reclamation is strictly best effort: only installed, idle
/// PaRuX-owned ONNX sessions can unload. These tests require no downloaded
/// models, work documents, third-party processes or Windows administrative APIs.
/// </summary>
public sealed class CorporateIdleRetrievalReclamationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Reclamation_can_only_run_in_corporate_when_no_index_job_is_active(
        bool corporate, bool indexingBusy, bool expected)
    {
        Assert.Equal(expected, KnowledgeJobService.MayReclaimIdleRetrieval(corporate, indexingBusy));
    }

    [Fact]
    public void Uninstalled_reranker_never_claims_to_return_ram_and_can_be_reused_safely()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-rerank-idle-" + Guid.NewGuid().ToString("N"));
        using var model = new RerankerService(root);
        Assert.False(model.IsLoaded);
        Assert.False(model.UnloadIfIdle(TimeSpan.Zero));
        Assert.Null(model.TryScore("query", "document"));
        Assert.False(model.UnloadIfIdle(TimeSpan.Zero));
        Assert.False(model.IsLoaded);
        Assert.True(model.LastUsedUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void Uninstalled_embedding_is_never_unloaded_by_pressure_reclamation()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-embed-idle-" + Guid.NewGuid().ToString("N"));
        using var model = new EmbeddingService(root);
        Assert.False(model.UnloadIfIdle(TimeSpan.Zero));
        Assert.False(model.IsLoaded);
    }

    [Fact]
    public void Owned_retrieval_reclamation_is_in_the_corporate_load_boundary_not_on_every_prompt()
    {
        string launch = Read("src", "PaRuX.Server", "LocalAiProcessManager.cs");
        string router = Read("src", "PaRuX.Server", "SmartRouterService.cs");
        string knowledge = Read("src", "PaRuX.Server", "Knowledge", "KnowledgeService.cs");
        string reranker = Read("src", "PaRuX.Server", "RerankerService.cs");
        string embedding = Read("src", "PaRuX.Server", "EmbeddingService.cs");

        Assert.Contains("if (!memory.Fits && corporate && !ct.IsCancellationRequested)", launch);
        Assert.Contains("ReleaseIdleRetrievalModelsForMemoryPressure()", launch);
        Assert.Contains("if (released > 0)", launch);
        Assert.Contains("ram = ExecutionResourceGuard.TryGetSystemRamSnapshot()", launch);
        Assert.Contains("results.All(candidate => candidate.HardwareCompatible != true)", router);
        Assert.Contains("ReleaseIdleRetrievalModelsForMemoryPressure()", router);
        Assert.Contains("corporateRam = _runtimeManager.SampleCorporateAutoRam()", router);
        Assert.Contains("candidate.HardwareCompatible = refreshed?.Compatible", router);
        Assert.Contains("CorporateCpuMemoryPlanner.ForModel(model, ram, memoryCeiling)", launch);
        Assert.Contains("Task.Delay(TimeSpan.FromMilliseconds(150), ct)", launch);
        Assert.Contains("_jobs.Any(job => !job.Value.Task.IsCompleted)", knowledge);
        Assert.Contains("_embeddings?.UnloadIfIdle(TimeSpan.Zero)", knowledge);
        Assert.Contains("_reranker?.UnloadIfIdle(TimeSpan.Zero)", knowledge);
        Assert.Contains("Volatile.Read(ref _inFlight) != 0", reranker);
        Assert.Contains("Interlocked.Increment(ref _inFlight)", reranker);
        Assert.Contains("Interlocked.Decrement(ref _inFlight)", reranker);
        Assert.Contains("CorporateMode.IsActive ? IsLoaded : TryEnsureLoaded()", reranker);
        Assert.Contains("CorporateMode.IsActive ? IsLoaded : TryEnsureLoaded()", embedding);
        Assert.DoesNotContain("EmptyWorkingSet", launch, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(params string[] relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaRuX.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(relative).ToArray()));
    }
}
