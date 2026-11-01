using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PaRuX.Server;

public sealed record GgufQuantizeJobStatus(
    string Id, string State, string Message, GgufQuantizeResult? Result, DateTime StartedUtc);

/// <summary>
/// One quantization at a time: it is CPU- and disk-heavy, so a second request is refused instead of queued,
/// and nothing runs while Gaming owns the machine. Status is kept in memory only (a restart forgets finished
/// jobs; the output file and its manifest are the durable record).
/// </summary>
public sealed class GgufQuantizeJobs
{
    private readonly GgufQuantizeService _service;
    private readonly Func<bool> _gamingActive;
    private readonly object _gate = new();
    private GgufQuantizeJobStatus? _current;
    private CancellationTokenSource? _cts;
    private Task? _running;

    public GgufQuantizeJobs(GgufQuantizeService service, LocalRuntimeService runtime)
        : this(service, () => runtime.GamingMode) { }

    internal GgufQuantizeJobs(GgufQuantizeService service, Func<bool> gamingActive)
    {
        _service = service;
        _gamingActive = gamingActive;
    }

    public GgufQuantizeJobStatus? Current { get { lock (_gate) return _current; } }

    public GgufQuantizeJobStatus Start(GgufQuantizeRequest request)
    {
        if (_gamingActive()) throw new InvalidOperationException("Leave Gaming before quantizing a model.");
        if (!_service.IsAvailable) throw new InvalidOperationException("llama-quantize is not installed in the managed runtime.");
        lock (_gate)
        {
            // Repeat the admission check under the same lock that Gaming-stop uses:
            // an export cannot sneak in after an earlier check but before Gaming hard-off.
            if (_gamingActive()) throw new InvalidOperationException("Leave Gaming before quantizing a model.");
            if (_current is { State: "running" }) throw new InvalidOperationException("A quantization is already running.");
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var started = new GgufQuantizeJobStatus(Guid.NewGuid().ToString("N"), "running", "Quantizing…", null, DateTime.UtcNow);
            _current = started;
            CancellationToken token = _cts.Token;
            _running = Task.Run(() => RunAsync(started, request, token));
            return started;
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_current is not { State: "running" }) return false;
            _cts?.Cancel();
            return true;
        }
    }

    /// <summary>Gaming cannot enter while a quantizer worker still holds CPU/disk resources.
    /// This uses the same cancellation token as the owner's Cancel button and waits for the
    /// worker to finish. A timeout or unconfirmed cancellation refuses Gaming entry.</summary>
    internal async Task<bool> StopForGamingAsync(CancellationToken ct, TimeSpan? maxWait = null)
    {
        Task? active;
        lock (_gate)
        {
            if (_current is not { State: "running" }) return true;
            _cts?.Cancel();
            active = _running;
        }
        if (active is null) return false;
        try
        {
            await active.WaitAsync(maxWait ?? TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
        // A generic failed job is not proof that cancellation stopped an external tool.
        return Current is { State: "cancelled" };
    }

    internal Task WhenIdleAsync(TimeSpan timeout) => Task.Run(() =>
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Current is not { State: "running" }) return;
            Thread.Sleep(10);
        }
    });

    private async Task RunAsync(GgufQuantizeJobStatus started, GgufQuantizeRequest request, CancellationToken ct)
    {
        GgufQuantizeJobStatus finished;
        try
        {
            GgufQuantizeResult result = await _service.QuantizeAsync(request, ct).ConfigureAwait(false);
            finished = started with { State = "completed", Message = "Quantization completed.", Result = result };
        }
        catch (OperationCanceledException) { finished = started with { State = "cancelled", Message = "Quantization was cancelled." }; }
        catch (GgufQuantizeException ex) { finished = started with { State = "failed", Message = ex.Message }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { finished = started with { State = "failed", Message = "The quantized file could not be written." }; }
        catch (Exception)
        { finished = started with { State = "failed", Message = "Quantization failed unexpectedly; inspect local logs before retrying." }; }
        lock (_gate)
        {
            if (_current?.Id == started.Id) _current = finished;
        }
    }
}

public static class GgufQuantizeEndpoints
{
    public static void MapGgufQuantizeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/lifecycle/quantize", (GgufQuantizeService service, GgufQuantizeJobs jobs) =>
            Results.Ok(new { available = service.IsAvailable, types = GgufQuantizeService.AllowedTypes.OrderBy(t => t), job = jobs.Current }))
            .RequireDesktopOwner();

        app.MapPost("/api/v1/lifecycle/quantize", (GgufQuantizeRequest request, GgufQuantizeJobs jobs) =>
        {
            try { return Results.Accepted(value: jobs.Start(request)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireDesktopOwner();

        app.MapDelete("/api/v1/lifecycle/quantize", (GgufQuantizeJobs jobs) =>
            jobs.Cancel() ? Results.Accepted() : Results.NotFound()).RequireDesktopOwner();
    }
}
