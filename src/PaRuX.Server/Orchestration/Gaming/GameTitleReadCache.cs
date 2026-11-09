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
    private DateTime _publishedUtc;
    private bool _invalidated;
    private long _revision;
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(15);

    /// <summary>Monotonic mutation generation. A scan started before an import or
    /// preference edit cannot publish an obsolete snapshot afterward.</summary>
    public long Revision { get { lock (_gate) return _revision; } }

    public void Invalidate()
    {
        lock (_gate)
        {
            _invalidated = true;
            _revision++;
        }
    }

    /// <summary>Only browse the stores once per short foreground window. Explicit
    /// refresh and a changed manual/emulator library bypass this cheap read.</summary>
    public bool TryReadRecent(out IReadOnlyList<GameTitleDto> titles)
    {
        lock (_gate)
        {
            if (_snapshot is not null && !_invalidated &&
                DateTime.UtcNow - _publishedUtc < FreshFor)
            {
                titles = _snapshot;
                return true;
            }
            titles = Array.Empty<GameTitleDto>();
            return false;
        }
    }

    public bool PublishIfUnchanged(long expectedRevision, IReadOnlyList<GameTitleDto> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        lock (_gate)
        {
            if (_revision != expectedRevision) return false;
            _snapshot = titles.ToArray();
            _publishedUtc = DateTime.UtcNow;
            _invalidated = false;
            _revision++;
            return true;
        }
    }

    public void Publish(IReadOnlyList<GameTitleDto> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        lock (_gate)
        {
            _snapshot = titles.ToArray();
            _publishedUtc = DateTime.UtcNow;
            _invalidated = false;
            _revision++;
        }
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
            _revision++;
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
                    Status = state.Status ?? string.Empty,
                    Score = state.Score,
                    Hidden = state.Hidden,
                    AddedUtc = state.AddedUtc,
                    Tags = state.Tags?.ToArray() ?? Array.Empty<string>(),
                    Collections = state.Collections?.ToArray() ?? Array.Empty<string>(),
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
