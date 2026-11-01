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

    /// <summary>Explicit owner edits remain visible in the cached Gaming library
    /// without rescanning installed stores. The persisted title overlay is the source
    /// of truth; callers pass its validated, post-write state.</summary>
    public void ApplyState(string titleKey, GameTitleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_snapshot is null) return;
            _snapshot = _snapshot.Select(title =>
            {
                if (!string.Equals(title.TitleKey, titleKey, StringComparison.Ordinal))
                    return title;
                string? selected = state.DefaultInstallationId;
                if (selected is null || !title.Installations.Any(i => i.InstallationId == selected))
                    selected = title.Installations.FirstOrDefault()?.InstallationId;
                return title with
                {
                    Favorite = state.Favorite,
                    Tags = (state.Tags ?? new()).ToArray(),
                    Collections = (state.Collections ?? new()).ToArray(),
                    PlaySeconds = state.PlaySeconds,
                    LastPlayedUtc = state.LastPlayedUtc,
                    DefaultInstallationId = selected
                };
            }).ToArray();
        }
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
