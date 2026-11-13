namespace PaRuX.Server;

/// <summary>
/// Typed refusal for local AI work that Gaming Mode hard-off stopped, or that was refused because Gaming is active.
/// It stays an <see cref="InvalidOperationException"/> so older callers keep working; endpoints map it to
/// 409 Conflict <c>{ code: "gaming_active" }</c> through <see cref="GamingHardOff.Conflict"/>.
/// </summary>
public class UnavailableDuringGamingException : InvalidOperationException
{
    public const string ErrorCode = "gaming_active";

    public UnavailableDuringGamingException(string message) : base(message) { }
    public UnavailableDuringGamingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A PaRuX-owned child process group that Gaming Mode must stop and later let start lazily again.</summary>
internal interface IGamingHardOffParticipant
{
    /// <summary>Close admission, cancel in-flight work and kill owned children. False means a child did not confirm exit.</summary>
    bool StopForGaming();

    /// <summary>Reopen lazy admission after Gaming left. Never starts anything by itself.</summary>
    void ReopenAfterGaming();
}

/// <summary>
/// The set of Gaming hard-off participants that are created on demand (retrieval servers, avatar and CLIP children).
/// The Gaming guard stops every registered participant on a Gaming start (and on each retry while the hard-off is
/// pending) and reopens them only after the Gaming marker is gone. Speech keeps its own, older wiring.
/// </summary>
internal sealed class GamingHardOffRegistry
{
    private readonly object _gate = new();
    private readonly List<IGamingHardOffParticipant> _participants = new();

    internal void Register(IGamingHardOffParticipant participant)
    {
        lock (_gate) if (!_participants.Contains(participant)) _participants.Add(participant);
    }

    internal void Unregister(IGamingHardOffParticipant participant)
    {
        lock (_gate) _participants.Remove(participant);
    }

    internal int Count { get { lock (_gate) return _participants.Count; } }

    /// <summary>Stops every participant (all are attempted). True only when every one confirmed exit.</summary>
    internal bool StopAll()
    {
        IGamingHardOffParticipant[] snapshot;
        lock (_gate) snapshot = _participants.ToArray();
        bool all = true;
        foreach (IGamingHardOffParticipant participant in snapshot)
        {
            try { if (!participant.StopForGaming()) all = false; }
            catch { all = false; }
        }
        return all;
    }

    internal void ReopenAll()
    {
        IGamingHardOffParticipant[] snapshot;
        lock (_gate) snapshot = _participants.ToArray();
        foreach (IGamingHardOffParticipant participant in snapshot)
        {
            try { participant.ReopenAfterGaming(); } catch { }
        }
    }
}

public static class GamingHardOff
{
    /// <summary>The one 409 body shape for every "unavailable during Gaming" refusal.</summary>
    public static IResult Conflict(UnavailableDuringGamingException exception) =>
        Results.Json(new { error = exception.Message, code = UnavailableDuringGamingException.ErrorCode },
            statusCode: StatusCodes.Status409Conflict);
}
