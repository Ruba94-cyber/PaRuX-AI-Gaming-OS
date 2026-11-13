using System.Diagnostics;

namespace PaRuX.Server;

/// <summary>
/// One Gaming hard-off boundary for a group of PaRuX-owned AI children: closed admission (nothing spawns after the
/// hard-off), a cancellation token that a Gaming start cancels, and a registered process set that is killed with exit
/// confirmation. It composes <see cref="SpeechProcessAdmission"/> (the primitive proven by the speech hard-off) and
/// is reused by the retrieval servers, avatar, CLIP judge and every managed installer/converter child, so there is a
/// single behaviour to reason about instead of one bespoke copy per service.
/// </summary>
internal sealed class GamingChildScope : IGamingHardOffParticipant, IDisposable
{
    private readonly SpeechProcessAdmission _admission;
    private readonly GamingHardOffRegistry? _registry;
    private readonly object _gate = new();
    private CancellationTokenSource _cts = new();

    internal GamingChildScope(string label, Func<bool> gamingActive, GamingHardOffRegistry? registry)
    {
        Label = label;
        _admission = new SpeechProcessAdmission(gamingActive, label);
        _registry = registry;
        _registry?.Register(this);
    }

    internal string Label { get; }

    /// <summary>Cancelled when Gaming starts; replaced when the hard-off reopens.</summary>
    internal CancellationToken Token { get { lock (_gate) return _cts.Token; } }

    internal bool IsClosed => _admission.IsClosed;

    internal int TrackedCount => _admission.TrackedCount;

    internal UnavailableDuringGamingException Refusal(Exception? inner = null) => inner == null
        ? new UnavailableDuringGamingException(
            $"Gaming Mode is active, so local {Label} work is unavailable.")
        : new UnavailableDuringGamingException(
            $"Gaming Mode is active, so local {Label} work is unavailable; the operation was cancelled and its process stopped.", inner);

    /// <summary>Closes admission, cancels in-flight work and kills registered children. False: a child did not confirm exit.</summary>
    public bool StopForGaming()
    {
        _admission.BlockForGaming();
        lock (_gate) { if (!_cts.IsCancellationRequested) _cts.Cancel(); }
        return _admission.BlockForGaming();
    }

    public void ReopenAfterGaming()
    {
        if (!_admission.ReopenAfterGaming()) return;
        lock (_gate)
        {
            if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }
        }
    }

    /// <summary>Refuses (typed) while Gaming is active, otherwise returns the admission ticket for this operation.</summary>
    internal long Capture()
    {
        if (_admission.IsClosed) throw Refusal();
        try { return _admission.Capture(); }
        catch (InvalidOperationException ex) { throw Refusal(ex); }
    }

    /// <summary>Starts the process inside the admission critical section, so a Gaming stop can never miss it.</summary>
    internal void StartOwned(Process process, long ticket)
    {
        try { _admission.StartOwned(process, ticket); }
        catch (Exception ex) when (_admission.IsClosed || !_admission.IsCurrent(ticket))
        {
            if (ex is UnavailableDuringGamingException) throw;
            throw Refusal(ex);
        }
    }

    internal void Unregister(Process process) => _admission.Unregister(process);

    internal void Check(long ticket) => _admission.Check(ticket);

    internal bool IsCurrent(long ticket) => _admission.IsCurrent(ticket);

    /// <summary>True when a failure belongs to a Gaming transition rather than to the caller or the operation itself.</summary>
    internal bool IsGamingInterruption(Exception ex, CancellationToken callerToken, long ticket) =>
        ex is UnavailableDuringGamingException
        || (!callerToken.IsCancellationRequested
            // Only a stop this scope actually issued counts (the generation moved or the token was cancelled by
            // StopForGaming). A raised marker alone must not relabel a genuine non-zero exit or a real failure.
            && (!_admission.IsCurrent(ticket) || Token.IsCancellationRequested));

    /// <summary>
    /// Runs <paramref name="body"/> under a token that the Gaming start cancels. Whatever the interruption looks like
    /// (cancelled token, killed child, closed pipe, stale ticket) the caller gets the one typed Gaming error; a cancel
    /// by the caller itself is never rewritten.
    /// </summary>
    internal async Task<T> RunAsync<T>(CancellationToken ct, Func<CancellationToken, long, Task<T>> body)
    {
        long ticket = Capture();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
        try
        {
            return await body(linked.Token, ticket).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not UnavailableDuringGamingException && IsGamingInterruption(ex, ct, ticket))
        {
            throw Refusal(ex);
        }
    }

    public void Dispose()
    {
        _registry?.Unregister(this);
    }
}

/// <summary>
/// One child process under a <see cref="GamingChildScope"/> (or a pass-through when no scope is installed, as in
/// tests). Dispose unregisters the process. <see cref="Token"/> is the caller token linked with the Gaming token.
/// </summary>
internal sealed class GamingChildLease : IDisposable
{
    private readonly GamingChildScope? _scope;
    private readonly Process _process;
    private readonly long _ticket;
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource? _linked;

    internal GamingChildLease(GamingChildScope? scope, Process process, long ticket, CancellationToken caller, CancellationTokenSource? linked)
    {
        _scope = scope;
        _process = process;
        _ticket = ticket;
        _caller = caller;
        _linked = linked;
    }

    internal CancellationToken Token => _linked?.Token ?? _caller;

    /// <summary>Call after the child ended or the wait was cancelled: a Gaming stop becomes the typed error, never a
    /// generic "exit code 1" or a bare cancellation. A cancel by the caller is left alone.</summary>
    internal void ThrowIfGamingInterrupted()
    {
        if (_scope != null && _scope.IsGamingInterruption(new OperationCanceledException(), _caller, _ticket))
            throw _scope.Refusal();
    }

    public void Dispose()
    {
        // A child that did not confirm exit stays registered so the guard keeps reporting it as unconfirmed.
        bool exited;
        try { exited = _process.HasExited; } catch { exited = true; }
        if (exited) _scope?.Unregister(_process);
        _linked?.Dispose();
    }
}

/// <summary>
/// Process-wide gate for managed installers, converters and one-shot AI children that are launched from static
/// helpers (uv/pip, LoRA conversion, OCR, training runtime, engine packs). Program composition installs the scope;
/// when none is installed (unit tests, tools) it is a pass-through that only starts the process.
/// </summary>
internal static class GamingChildGate
{
    private static volatile GamingChildScope? _ambient;
    private static readonly AsyncLocal<GamingChildScope?> Override = new();

    internal static GamingChildScope? Ambient => Override.Value ?? _ambient;

    internal static void Install(GamingChildScope? scope) => _ambient = scope;

    private static int _warnedUngated;

    /// <summary>Logs once (warning) when a child is launched with no ambient scope outside a test host: that launch is not
    /// refused during Gaming and is not killed on a Gaming start, so it must never happen silently in production.</summary>
    private static void WarnOnceUngated()
    {
        if (Interlocked.Exchange(ref _warnedUngated, 1) != 0) return;
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "xunit.core" or "xunit.v3.core")) return;
        Trace.TraceWarning("PaRuX Gaming hard-off: a managed child was launched with no ambient Gaming scope installed; "
            + "it is not gated against Gaming Mode.");
    }

    /// <summary>Test seam: replaces the ambient scope for the current async flow only (no cross-test interference).</summary>
    internal static IDisposable UseForCurrentFlow(GamingChildScope? scope)
    {
        GamingChildScope? previous = Override.Value;
        Override.Value = scope;
        return new Restore(() => Override.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>
    /// Starts <paramref name="process"/> under the ambient scope: refused (typed) while Gaming is active, registered so a
    /// Gaming start kills it. The returned lease carries the token to wait on.
    /// </summary>
    internal static GamingChildLease StartOwned(Process process, CancellationToken ct, GamingChildScope? scope = null)
    {
        scope ??= Ambient;
        if (scope == null)
        {
            WarnOnceUngated();
            if (!process.Start()) throw new InvalidOperationException("The child process could not start.");
            return new GamingChildLease(null, process, 0, ct, null);
        }
        long ticket = scope.Capture();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, scope.Token);
        try
        {
            scope.StartOwned(process, ticket);
        }
        catch
        {
            linked.Dispose();
            throw;
        }
        return new GamingChildLease(scope, process, ticket, ct, linked);
    }
}
