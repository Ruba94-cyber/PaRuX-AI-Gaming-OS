using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Last successfully assembled title catalogue, held only in memory. Console search
/// while Gaming uses this snapshot instead of touching Steam/Epic/GOG manifests.
/// No hidden scan, periodic polling, persisted absolute paths or network access.
/// A cache miss is explicit: it must never look like an empty game library.
/// </summary>
public sealed class GameTitleReadCache
{
    private readonly object _gate = new();
    private IReadOnlyList<GameTitleDto>? _snapshot;

    public void Publish(IReadOnlyList<GameTitleDto> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        lock (_gate) _snapshot = titles.ToArray();
    }

    public bool TryRead(out IReadOnlyList<GameTitleDto> titles)
    {
        lock (_gate)
        {
            if (_snapshot is null)
            {
                titles = Array.Empty<GameTitleDto>();
                return false;
            }
            titles = _snapshot;
            return true;
        }
    }
}
