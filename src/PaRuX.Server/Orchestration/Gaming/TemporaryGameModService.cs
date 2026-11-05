using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Reversible AI-assisted TEXT-ONLY config changes for PaRuX-owned portable
/// games, while the exact game process is suspended by Quick Resume.
/// No memory injection, DLL hooks, online game modifications, EXE patching,
/// DRM changes, scripting or network I/O. All writes need a fresh owner preview
/// and explicit separate approval. The AI cannot approve a preview.
/// The mod remains active until the owner restores it. No false auto-rollback
/// guarantee is made if PaRuX or the OS crashes.
/// </summary>
public sealed class TemporaryGameModService
{
    private const int MaxTextBytes = 64 * 1024;
    private const int MaxSessions = 16;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ini", ".cfg", ".json", ".txt" };
    private sealed record Proposal(
        string Id, string ManualGameId, int ProcessId, DateTime StartedUtc,
        string GameName, string Target, string RelativePath, string OriginalSha,
        string ProposedText, string ModifiedSha, DateTime ExpiresUtc);
    private sealed record Journal(
        string Id, string ManualGameId, int ProcessId, DateTime StartedUtc,
        string GameName, string Target, string RelativePath,
        string OriginalSha, string ModifiedSha, string OriginalBase64, DateTime AppliedUtc);
    private readonly ManualGameLibrary _library;
    private readonly string _managedRoot;
    private readonly string _journalRoot;
    private readonly Func<int, DateTime, string, bool> _pausedIdentity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Proposal> _proposals = new();

    public TemporaryGameModService(ManualGameLibrary library, QuickResumeService quickResume,
        LocalRuntimeService runtime)
        : this(library, Path.Combine(PaRuXDataRoot.Path, "server", "portable-games"),
            Path.Combine(PaRuXDataRoot.Path, "server", "temporary-game-mods"),
            (pid, started, exe) => !runtime.GamingMode && PausedProcessMatches(quickResume, pid, started, exe)) { }

    internal TemporaryGameModService(ManualGameLibrary library, string managedRoot, string journalRoot,
        Func<int, DateTime, string, bool> pausedIdentity)
    {
        _library = library;
        _managedRoot = Path.GetFullPath(managedRoot);
        _journalRoot = Path.GetFullPath(journalRoot);
        _pausedIdentity = pausedIdentity;
    }

    private static bool PausedProcessMatches(QuickResumeService service, int pid, DateTime startedUtc, string exe)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows()) return false;
        if (!service.List().Apps.Any(x => x.ProcessId == pid && x.Suspended
            && QuickResumePolicy.SameProcess(x.StartedUtc, startedUtc)
            && !GameAutoSuspendPolicy.IsExcludedProcess(x.ProcessName)))
            return false;
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited
                && QuickResumePolicy.SameProcess(process.StartTime.ToUniversalTime(), startedUtc)
                && string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""),
                    Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
    }

    internal static bool SafeRelativeConfig(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 ||
            path.Any(char.IsControl) || Path.IsPathRooted(path) || path.Contains(':'))
            return false;
        string normalized = path.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        return segments.Length is >= 1 and <= 12
            && segments.All(x => x.Length > 0 && x is not "." and not ".."
                && !x.EndsWith(' ') && !x.EndsWith('.'))
            && Extensions.Contains(Path.GetExtension(normalized))
            && !segments.Any(x => x.StartsWith(".parux-", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateText(string relative, string content)
    {
        if (content is null || Encoding.UTF8.GetByteCount(content) > MaxTextBytes ||
            content.Contains('\0'))
            throw new ArgumentException("The proposed game configuration is too large or not plain text.");
        if (string.Equals(Path.GetExtension(relative), ".json", StringComparison.OrdinalIgnoreCase))
        {
            try { using JsonDocument doc = JsonDocument.Parse(content); }
            catch (JsonException) { throw new ArgumentException("The proposed JSON is invalid."); }
        }
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Digest(string content) => Digest(Encoding.UTF8.GetBytes(content));

    private string ResolveTarget(string gameId, string relative, out ManualGameDto game)
    {
        if (!SafeRelativeConfig(relative)) throw new ArgumentException("Choose a safe relative config file (.ini/.cfg/.json/.txt).");
        game = _library.List().SingleOrDefault(x => x.Id == gameId && x.Kind == ManualEntryKinds.Game)
            ?? throw new InvalidOperationException("This game is not registered in your manual library.");
        string exe = Path.GetFullPath(game.ExecutablePath);
        string parent = Path.GetDirectoryName(exe)!;
        string? managedGameRoot = null;
        for (string? candidate = parent; candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            if (!string.Equals(Path.GetDirectoryName(candidate), _managedRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            string name = Path.GetFileName(candidate);
            if (name.StartsWith("game-", StringComparison.OrdinalIgnoreCase) &&
                name.Length == 37 && name[5..].All(Uri.IsHexDigit))
                managedGameRoot = candidate;
            break;
        }
        if (managedGameRoot is null || !ManualGameLibrary.IsSafeDirectory(managedGameRoot) ||
            !string.Equals(Path.GetFullPath(_managedRoot), _managedRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("Temporary mods are available only for PaRuX-managed portable games.");
        if (!exe.StartsWith(managedGameRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The executable does not belong to the managed game folder.");
        string target = Path.GetFullPath(Path.Combine(managedGameRoot,
            relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(managedGameRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !ManualGameLibrary.IsSafeDirectory(Path.GetDirectoryName(target)!))
            throw new InvalidOperationException("The configuration is outside the managed game directory.");
        var file = new FileInfo(target);
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length > MaxTextBytes)
            throw new InvalidOperationException("Configuration file is unavailable, linked or too large.");
        return target;
    }

    private void RequirePause(ManualGameDto game, int pid, DateTime startedUtc)
    {
        if (pid < 1 || GameAutoSuspendPolicy.IsExcludedProcess(Path.GetFileNameWithoutExtension(game.ExecutablePath))
            || !_pausedIdentity(pid, startedUtc, game.ExecutablePath))
            throw new InvalidOperationException("PaRuX must verify this exact offline game is currently paused by Quick Resume.");
    }

    public GameModPreviewDto Preview(GameModProposalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.OfflineSinglePlayerConfirmed)
            throw new ArgumentException("Confirm that this portable game is offline, single-player and yours to modify.");
        string target = ResolveTarget(request.ManualGameId, request.RelativePath, out ManualGameDto game);
        RequirePause(game, request.ProcessId, request.StartedUtc);
        ValidateText(request.RelativePath, request.ProposedText);
        byte[] oldBytes = File.ReadAllBytes(target);
        string original;
        try { original = new UTF8Encoding(false, true).GetString(oldBytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Only UTF-8 text files can be modified."); }
        ValidateText(request.RelativePath, original);
        string sha = Digest(oldBytes);
        string newSha = Digest(request.ProposedText);
        if (sha == newSha) throw new ArgumentException("The proposal does not change this configuration.");
        lock (_gate)
        {
            EnsureJournalFolder();
            if (ReadJournals().Any(x => string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This file has an active temporary mod. Restore it before proposing another.");
            DateTime now = DateTime.UtcNow;
            foreach (string old in _proposals.Where(x => x.Value.ExpiresUtc <= now).Select(x => x.Key).ToArray())
                _proposals.Remove(old);
            if (_proposals.Count >= 12) throw new InvalidOperationException("Too many unreviewed mod previews.");
            var proposal = new Proposal(Guid.NewGuid().ToString("N"), game.Id, request.ProcessId,
                request.StartedUtc, game.Name, target, request.RelativePath, sha,
                request.ProposedText, newSha, now + PreviewLifetime);
            _proposals.Add(proposal.Id, proposal);
            return new GameModPreviewDto(proposal.Id, game.Name, request.RelativePath,
                sha, original, proposal.ProposedText, proposal.ExpiresUtc,
                "Review the full before/after text. Some games only read configs at startup: a paused game may not reload it. "
                + "No executable or memory is touched. You can restore while the game is paused or closed.");
        }
    }

    public GameTemporaryModDto Apply(GameModApplyRequest request)
    {
        if (request is null || !request.Consent)
            throw new ArgumentException("Explicit owner approval of the previously shown preview is required.");
        lock (_gate)
        {
            if (!_proposals.TryGetValue(request.PreviewId, out Proposal? proposal) ||
                proposal.ExpiresUtc <= DateTime.UtcNow)
                throw new InvalidOperationException("The mod preview expired. Review the game file again.");
            // Consent token is single use even when applying fails.
            _proposals.Remove(request.PreviewId);
            using FileStream lease = AcquireLease();
            string target = ResolveTarget(proposal.ManualGameId, proposal.RelativePath, out ManualGameDto game);
            if (!string.Equals(target, proposal.Target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The game path changed since the preview.");
            RequirePause(game, proposal.ProcessId, proposal.StartedUtc);
            if (ReadJournals().Any(x => string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The config already has an active temporary mod.");
            byte[] original = File.ReadAllBytes(target);
            if (Digest(original) != proposal.OriginalSha)
                throw new InvalidOperationException("The config changed since preview; nothing was overwritten.");
            byte[] updated = Encoding.UTF8.GetBytes(proposal.ProposedText);
            var journal = new Journal(Guid.NewGuid().ToString("N"), proposal.ManualGameId,
                proposal.ProcessId, proposal.StartedUtc, game.Name, target, proposal.RelativePath,
                proposal.OriginalSha, proposal.ModifiedSha, Convert.ToBase64String(original), DateTime.UtcNow);
            string journalPath = JournalPath(journal.Id);
            string temp = target + ".parux-mod-" + journal.Id + ".tmp";
            try
            {
                WriteJournal(journalPath, journal); // rollback receipt BEFORE modifying a game file
                File.WriteAllBytes(temp, updated);
                RequirePause(game, proposal.ProcessId, proposal.StartedUtc);
                if (Digest(File.ReadAllBytes(target)) != proposal.OriginalSha)
                    throw new InvalidOperationException("The game file changed during preparation.");
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            return ToDto(journal);
        }
    }

    public IReadOnlyList<GameTemporaryModDto> List()
    {
        lock (_gate)
        {
            EnsureJournalFolder();
            return ReadJournals().Select(ToDto).ToArray();
        }
    }

    public bool Restore(GameModRestoreRequest request)
    {
        if (request is null || !request.Consent)
            throw new ArgumentException("Restoring game files requires explicit owner confirmation.");
        lock (_gate)
        {
            using FileStream lease = AcquireLease();
            if (request.ModId is not { Length: 32 } ||
                !request.ModId.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid mod id.");
            string file = JournalPath(request.ModId);
            if (!File.Exists(file)) return false;
            Journal journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(file))
                ?? throw new InvalidDataException("Temporary mod recovery receipt is invalid.");
            string target = ResolveTarget(journal.ManualGameId, journal.RelativePath, out ManualGameDto game);
            if (!string.Equals(target, journal.Target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The game directory changed; restore was not attempted.");
            // Only restore when the same game is paused or has exited. Never
            // mutate the config while it may be actively writing saves/settings.
            if (ProcessStillAlive(journal.ProcessId, journal.StartedUtc))
                RequirePause(game, journal.ProcessId, journal.StartedUtc);
            byte[] original = Convert.FromBase64String(journal.OriginalBase64);
            if (Digest(original) != journal.OriginalSha)
                throw new InvalidDataException("Original file backup failed integrity validation.");
            byte[] current = File.ReadAllBytes(target);
            string currentHash = Digest(current);
            if (currentHash != journal.ModifiedSha && currentHash != journal.OriginalSha)
                throw new InvalidOperationException("Game or user changed this file after the mod; manual conflict resolution is required.");
            if (currentHash != journal.OriginalSha)
            {
                string temp = target + ".parux-restore-" + journal.Id + ".tmp";
                try { File.WriteAllBytes(temp, original); File.Move(temp, target, overwrite: true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            File.Delete(file);
            return true;
        }
    }

    private static bool ProcessStillAlive(int pid, DateTime started)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited && QuickResumePolicy.SameProcess(process.StartTime.ToUniversalTime(), started);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { return false; }
    }

    private static GameTemporaryModDto ToDto(Journal x) => new(
        x.Id, x.GameName, x.RelativePath, x.AppliedUtc, x.OriginalSha, x.ModifiedSha,
        "Temporary config edit. Restore explicitly after play; the game may need restart to load config changes.");

    private string JournalPath(string id) => Path.Combine(_journalRoot, "mod-" + id + ".json");

    private void EnsureJournalFolder()
    {
        Directory.CreateDirectory(_journalRoot);
        if (!ManualGameLibrary.IsSafeDirectory(_journalRoot))
            throw new InvalidDataException("Linked temporary mod backup folder is unsafe.");
    }

    private FileStream AcquireLease()
    {
        EnsureJournalFolder();
        string path = Path.Combine(_journalRoot, ".parux-mod.lock");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked temporary mod lock is unsafe.");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(5))
            { Thread.Sleep(30); }
        }
    }

    private List<Journal> ReadJournals()
    {
        if (!Directory.Exists(_journalRoot)) return new();
        var paths = Directory.EnumerateFiles(_journalRoot, "mod-*.json", SearchOption.TopDirectoryOnly)
            .Take(MaxSessions + 1).ToArray();
        if (paths.Length > MaxSessions) throw new InvalidOperationException("Too many temporary mods.");
        var list = new List<Journal>();
        foreach (string path in paths)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTextBytes * 3 ||
                info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Temporary mod backup is not a regular bounded file.");
            Journal? entry;
            try { entry = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path)); }
            catch (JsonException ex) { throw new InvalidDataException("Temporary mod backup is corrupted; no game file was touched.", ex); }
            if (entry is null || entry.Id.Length != 32 || !entry.Id.All(Uri.IsHexDigit)
                || Path.GetFileName(path) != "mod-" + entry.Id + ".json")
                throw new InvalidDataException("Temporary mod journal identity is invalid.");
            list.Add(entry);
        }
        return list;
    }

    private static void WriteJournal(string path, Journal entry)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
