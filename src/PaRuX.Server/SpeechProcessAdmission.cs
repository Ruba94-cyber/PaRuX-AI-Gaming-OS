using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PaRuX.Server;

/// <summary>
/// Machine-local admission for PaRuX-owned speech child processes. Start and registration
/// are one critical section. Gaming invalidates earlier tickets and kills only registered
/// children; neither a window search nor a PID guessed from the process table is trusted.
/// This does not claim that a Kill request has already released the GPU or exited.
/// </summary>
internal sealed class SpeechProcessAdmission
{
    private static readonly ConditionalWeakTable<LocalRuntimeService, SpeechProcessAdmission> RuntimeAdmissions = new();
    internal static SpeechProcessAdmission For(LocalRuntimeService runtime) =>
        RuntimeAdmissions.GetValue(runtime, static r => new SpeechProcessAdmission(() => r.GamingMode));

    private readonly object _gate = new();
    private readonly Func<bool> _isGaming;
    private readonly string _label;
    private readonly HashSet<Process> _owned = new();
    private long _generation;
    private bool _blocked;

    /// <param name="label">What the admission guards, used only in messages. Speech is the original user; the same
    /// primitive now closes admission for every other PaRuX-owned AI child (retrieval servers, avatar, CLIP judge).</param>
    internal SpeechProcessAdmission(Func<bool> isGaming, string label = "speech")
    {
        _isGaming = isGaming ?? throw new ArgumentNullException(nameof(isGaming));
        _label = label;
    }

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
                throw new OperationCanceledException($"The {_label} request is no longer admitted after a Gaming transition.");
        }
    }

    internal void StartOwned(Process process, long generation, Func<Process, bool>? startOverrideForTests = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            EnsureAllowed();
            if (generation != _generation)
                throw new OperationCanceledException($"Stale {_label} process start was cancelled by Gaming Mode.");
            // No Gaming Stop can pass through this gate between Process.Start and ownership
            // registration. A failed Start never adds the process to the owned set.
            if (!(startOverrideForTests?.Invoke(process) ?? process.Start()))
                throw new InvalidOperationException($"The managed {_label} child process could not start.");
            _owned.Add(process);
        }
    }

    internal void Unregister(Process process)
    {
        lock (_gate) _owned.Remove(process);
    }

    /// <summary>Issue the stop and confirm that each registered child exited within
    /// a bounded wait. False is an unconfirmed release, never success; GPU memory still
    /// requires separate physical telemetry. Do not remove a still-running owned process.</summary>
    internal bool BlockForGaming()
    {
        Process[] children;
        lock (_gate)
        {
            _blocked = true;
            _generation++;
            children = _owned.ToArray();
        }

        bool allExited = true;
        foreach (Process child in children)
        {
            try
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
                if (!child.WaitForExit(1200)) allExited = false;
            }
            catch (InvalidOperationException)
            {
                // A disposed or never-started Process object has no live child behind it: treat it as exited and forget it.
                Unregister(child);
            }
            catch { allExited = false; }
        }
        return allExited;
    }

    /// <returns>True when admission is open again; false while Gaming is still active or an old child has not exited.</returns>
    internal bool ReopenAfterGaming()
    {
        lock (_gate)
        {
            if (_isGaming()) return false;
            // Idempotent: the guard calls this on every tick while Gaming is off. Nothing to reopen when admission is open,
            // and the generation must not move (that would cancel healthy in-flight requests).
            if (!_blocked) return true;
            // Keep speech disabled if a previous generation's owned child did
            // not exit. An old worker must never overlap newly admitted inference.
            foreach (Process child in _owned.ToArray())
            {
                bool exited;
                try { exited = child.HasExited; }
                catch (InvalidOperationException) { exited = true; } // disposed Process: nothing left to wait for
                catch { return false; }
                if (!exited) return false;
                _owned.Remove(child);
                try { child.Dispose(); } catch { }
            }
            _generation++; // Requests from before Gaming cannot reacquire an old ticket.
            _blocked = false;
            return true;
        }
    }

    /// <summary>True while speech admission is closed (Gaming active or hard-off not yet reopened).</summary>
    internal bool IsClosed
    {
        get { lock (_gate) return _blocked || _isGaming(); }
    }

    /// <summary>False when the ticket was issued before the latest Gaming transition.</summary>
    internal bool IsCurrent(long generation)
    {
        lock (_gate) return generation == _generation;
    }

    internal int TrackedCount
    {
        get { lock (_gate) return _owned.Count; }
    }

    private void EnsureAllowed()
    {
        if (_blocked || _isGaming())
            throw new InvalidOperationException($"Gaming Mode blocks local {_label} process admission.");
    }
}
