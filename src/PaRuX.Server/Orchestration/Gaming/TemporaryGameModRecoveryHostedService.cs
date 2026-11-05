using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Low-frequency, owner-local cleanup of approved temporary text mods.
/// Never runs during active Gaming; an exited process, verified receipt and
/// unchanged file SHA are required before the original can be restored.
/// </summary>
internal sealed class TemporaryGameModRecoveryHostedService : BackgroundService
{
    private readonly TemporaryGameModService _mods;
    private readonly LocalRuntimeService _runtime;
    private readonly ILogger<TemporaryGameModRecoveryHostedService> _logger;

    public TemporaryGameModRecoveryHostedService(TemporaryGameModService mods,
        LocalRuntimeService runtime, ILogger<TemporaryGameModRecoveryHostedService> logger)
    {
        _mods = mods;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_runtime.GamingMode)
                {
                    int restored = _mods.RestoreExited();
                    if (restored > 0)
                        _logger.LogInformation("Restored {Count} expired game mod files after game exit.", restored);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException or InvalidDataException or System.Security.SecurityException)
            {
                // No crash loop or forced overwrite. The owner can use Restore
                // AI Mods to inspect remaining recovery records and conflicts.
                _logger.LogWarning("Temporary game mod automatic recovery was deferred: {Error}", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
