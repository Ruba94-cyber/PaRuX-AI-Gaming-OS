using System.Diagnostics;

namespace PaRuX.Server;

/// <summary>
/// Machine-local admission for PaRuX-owned speech child processes. Start and registration
/// are one critical section. Gaming invalidates earlier tickets and kills only registered
/// children; neither a window search nor a PID guessed from the process table is trusted.
/// This does not claim that a Kill request has already released the GPU or exited.
/// </summary>
internal sealed class SpeechProcessAdmission
{
    private readonly object _gate = new();
    private readonly Func<bool> _isGaming;
    private readonly HashSet<Process> _owned = new();
    private long _generation;
    private bool _blocked;

    internal SpeechProcessAdmission(Func<bool> isGaming) =>
        _isGaming = isGaming ?? throw new ArgumentNullException(nameof(isGaming));

    internal long Capture()
    {
        lock (_gate)
        {
            EnsureAllowed();
            return _generation;
        }
    }

    internal void Check(long generation)
    {
        lock (_gate)
        {
            EnsureAllowed();
            if (generation != _generation)
                throw new OperationCanceledException("Speech request is no longer admitted after a Gaming transition.");
        }
    }

    internal void StartOwned(Process process, long generation, Func<Process, bool>? startOverrideForTests = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            EnsureAllowed();
            if (generation != _generation)
                throw new OperationCanceledException("Stale speech worker start was cancelled by Gaming Mode.");
            // No Gaming Stop can pass through this gate between Process.Start and ownership
            // registration. A failed Start never adds the process to the owned set.
            if (!(startOverrideForTests?.Invoke(process) ?? process.Start()))
                throw new InvalidOperationException("The managed speech child process could not start.");
            _owned.Add(process);
        }
    }

    internal void Unregister(Process process)
    {
        lock (_gate) _owned.Remove(process);
    }

    internal void BlockForGaming()
    {
        Process[] children;
        lock (_gate)
        {
            _blocked = true;
            _generation++;
            children = _owned.ToArray();
        }

        foreach (Process child in children)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch { /* Stop is best-effort; Windows integration must verify actual child exit. */ }
        }
    }

    internal void ReopenAfterGaming()
    {
        lock (_gate)
        {
            if (_isGaming()) return;
            _generation++; // Requests from before Gaming cannot reacquire an old ticket.
            _blocked = false;
        }
    }

    internal int TrackedCount
    {
        get { lock (_gate) return _owned.Count; }
    }

    private void EnsureAllowed()
    {
        if (_blocked || _isGaming())
            throw new InvalidOperationException("Gaming Mode blocks local speech process admission.");
    }
}
