using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>
/// Truthful Training Lab runtime boundary used by PaRuX first-party clients. Legacy v1 routes stay
/// available for compatibility, but v2 is the production path for installation, capability status
/// and new job admission: all three use the same pinned runtime identity and backend policy.
/// </summary>
public static class TrainingRuntimeV2Endpoints
{
    private static string RuntimeRoot => Path.Combine(PaRuXDataRoot.Path, "ai-runtimes", "training-lab");
    private static string VenvRoot => Path.Combine(RuntimeRoot, ".venv");

    // Removing the runtime is desktop-owner authority. Loopback alone is not enough: a browser session through a
    // same-machine reverse proxy also arrives from 127.0.0.1, and v1 already refuses it on this same ground.
    private static bool IsDesktopOwner(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } remote && System.Net.IPAddress.IsLoopback(remote)
        && context.Items["PaRuXOwner"] as string == "desktop";

    public static void MapTrainingRuntimeV2Endpoints(this WebApplication app)
    {
        // Mapping occurs during Core startup. Resolve the singleton now rather than waiting for the
        // first Training Lab UI/API request so persisted interrupted jobs are reconciled immediately
        // after restart (and remain deferred, not launched, while Gaming Mode is active).
        _ = app.Services.GetRequiredService<TrainingLabService>();

        app.MapGet("/api/v2/training/engine", async (CancellationToken ct) =>
        {
            TrainingRuntimeStatus status = await TrainingRuntimeCatalog
                .GetStatusAsync(RuntimeRoot, VenvRoot, ct).ConfigureAwait(false);
            return Results.Ok(ToContract(status));
        });

        // The install side provisions roughly 5 GB of managed Python - over 4 GB of it torch - and
        // until now nothing could undo that from inside PaRuX. A product that installs large
        // third-party stacks on demand has to be able to give the disk back on demand too.
        app.MapDelete("/api/v2/training/engine", async (
            TrainingLabService training,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!IsDesktopOwner(context))
                return Results.Json(new { error = "Removing the training runtime is desktop-only." },
                    statusCode: StatusCodes.Status403Forbidden);

            TrainingEngineRemovalResult result = await training.UninstallEngineAsync(ct).ConfigureAwait(false);
            return result.Removed ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapPost("/api/v2/training/engine/install", async (CancellationToken ct) =>
        {
            try
            {
                string log = await TrainingRuntimeCatalog
                    .InstallAsync(RuntimeRoot, VenvRoot, ct).ConfigureAwait(false);
                TrainingRuntimeStatus status = await TrainingRuntimeCatalog
                    .GetStatusAsync(RuntimeRoot, VenvRoot, ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    installed = status.Installed,
                    ready = status.Ready,
                    status = ToContract(status),
                    log = Trim(log)
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (UnavailableDuringGamingException gaming) { return GamingHardOff.Conflict(gaming); }
            catch (TrainingRuntimeRefusalException ex)
            {
                return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or TimeoutException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireDesktopOwner();

        app.MapPost("/api/v2/training/jobs", async (
            TrainingJobRequest request,
            TrainingLabService training,
            RuntimeManagerService runtimeManager,
            CancellationToken ct) =>
        {
            try
            {
                ArgumentNullException.ThrowIfNull(request);
                TrainingRuntimeStatus runtimeStatus = await TrainingRuntimeCatalog
                    .GetStatusAsync(RuntimeRoot, VenvRoot, ct).ConfigureAwait(false);
                TrainingRuntimeJobGate.ValidateStatus(runtimeStatus, request.Method);

                // The desktop and CLI inspect a run first, but no direct API client may
                // bypass memory/shape refusal by skipping that advisory preflight.
                // This is especially important for opt-in 16-bit LoRA and unpaired KTO.
                TrainingDatasetShape.Shape dataset = TrainingDatasetShape.Inspect(request.TrainJsonlPath);
                RuntimeManagerStatus hardware = await runtimeManager.GetStatusAsync(ct).ConfigureAwait(false);
                double availableGib = hardware.GpuDetected && hardware.VramTotalMib > 0
                    ? hardware.VramTotalMib / 1024.0
                    : hardware.RamTotalBytes / 1024.0 / 1024.0 / 1024.0 * 0.5;
                TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
                    dataset, request.Method, request.BaseModel,
                    request.LoraRank, request.MaxSteps, request.LearningRate,
                    availableGib, request.Precision, request.TargetPreset, request.MaxSequenceLength);
                if (!plan.CanStart)
                    return Results.BadRequest(new
                    {
                        error = "This training run cannot start with the submitted dataset, model or resource budget.",
                        plan.DetectedMethod,
                        plan.EstimatedGib,
                        plan.AvailableGib,
                        findings = plan.Blocking.Select(x => new { field = x.Field, message = x.Message }).ToArray()
                    });

                // TrainingLabService remains the single owner of full dataset checks,
                // physical KTO microbatch validation, durable jobs and Gaming hard-off.
                return Results.Ok(training.CreateJob(request));
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (TrainingRuntimeRefusalException ex)
            {
                return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).RequireDesktopOwner();
    }

    internal static TrainingEngineStatus ToContract(TrainingRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string backend = status.Probe?.Backend ?? status.Manifest?.Backend ?? "unknown";
        return new TrainingEngineStatus
        {
            Installed = status.Installed,
            Ready = status.Ready,
            PythonVersion = status.Probe?.PythonVersion ?? status.Manifest?.PythonVersion,
            UnslothVersion = status.Probe?.UnslothVersion ?? status.Manifest?.UnslothVersion,
            TorchVersion = status.Probe?.TorchVersion ?? status.Manifest?.TorchVersion,
            CudaVerified = status.Ready && string.Equals(backend, "cuda", StringComparison.Ordinal),
            Backend = backend,
            ResolvedPackagesSha256 = status.Manifest?.ResolvedPackagesSha256,
            Detail = status.Detail,
            AvailableMethods = status.AvailableMethods.ToList(),
            HardwareProfile = TrainingHardwareProfile.Classify(status.Installed, backend),
            UnsupportedMethods = TrainingHardwareProfile
                .Unsupported(status.Installed, backend, status.AvailableMethods)
                .Select(item => new TrainingUnsupportedMethod { Method = item.Method, Reason = item.Reason })
                .ToList()
        };
    }

    private static string Trim(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        return text.Length <= 12_000 ? text : text[^12_000..];
    }
}
