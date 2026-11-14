namespace PaRuX.Server;

/// <summary>
/// Typed refusal for local speech work (STT/TTS) while Gaming Mode is hard-off, or for an in-flight request that a
/// Gaming start interrupted. It stays an <see cref="InvalidOperationException"/> so older callers keep working;
/// endpoints map it to 409 Conflict like the other Gaming-gated services.
/// </summary>
public sealed class SpeechUnavailableDuringGamingException : UnavailableDuringGamingException
{
    public SpeechUnavailableDuringGamingException(string message) : base(message) { }
    public SpeechUnavailableDuringGamingException(string message, Exception inner) : base(message, inner) { }
}
