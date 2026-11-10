namespace PaRuX.Server;

/// <summary>Why a text-profile request reached the process manager.</summary>
public enum TextStartIntent
{
    /// <summary>Ensure/Auto/On: keep any healthy resident of the same model (the long-standing idempotent start).</summary>
    Ensure,
    /// <summary>An explicit Standard/Uncensored request: reuse the resident only when it is provably identical, otherwise replace it.</summary>
    ProfileRequest,
    /// <summary>An explicit restart: always stop the resident and launch again.</summary>
    ForcedRestart
}

/// <summary>
/// Decides whether a profile request may be answered by the text runtime that is already running. Pure, so every
/// condition is unit-tested without starting a process. Reuse requires the same model file, the same executable, the
/// exact same launch arguments (every setting that reaches the command line) and a live, healthy process; Gaming
/// and an explicit restart never reuse.
/// </summary>
internal static class WarmTextReusePolicy
{
    internal sealed record Resident(string ModelPath, string Executable, IReadOnlyList<string> Arguments);

    internal sealed record Candidate(string Executable, IReadOnlyList<string> Arguments);

    internal static bool CanReuse(
        TextStartIntent intent, bool gamingActive, bool processHealthy,
        Resident? resident, string requestedModelPath, IEnumerable<Candidate> plannedLaunches)
    {
        if (intent != TextStartIntent.ProfileRequest || gamingActive || !processHealthy || resident is null) return false;
        if (!SamePath(resident.ModelPath, requestedModelPath)) return false;
        // The resident may be running on a fallback rung (flash attention off, reduced offload...). It is identical when
        // the launch that produced it is one the current request would produce again.
        foreach (Candidate candidate in plannedLaunches)
        {
            if (string.Equals(candidate.Executable, resident.Executable, StringComparison.OrdinalIgnoreCase)
                && candidate.Arguments.SequenceEqual(resident.Arguments, StringComparer.Ordinal))
                return true;
        }
        return false;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
