using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>One line of the local audit log. <see cref="Hash"/> covers every other field plus <see cref="PreviousHash"/>.</summary>
public sealed class AuditEntry
{
    public long Sequence { get; set; }
    public DateTime TimestampUtc { get; set; }
    /// <summary>Short machine name, e.g. "plugin.install".</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>What it applied to (a plugin id, a setting name). Never a secret, a path to private data or message content.</summary>
    public string Subject { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string PreviousHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}

public sealed class AuditVerification
{
    public bool Intact { get; set; }
    public int Entries { get; set; }
    /// <summary>Sequence number of the first entry that does not match, or null when the chain is intact.</summary>
    public long? FirstBrokenSequence { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Append-only, hash-chained local record of security-relevant actions (plugin installs and removals so far). Each entry carries
/// the hash of the one before it, so editing, deleting or reordering a line is detected by <see cref="Verify"/>. It is a
/// tamper-evident record on this PC, not a remote or signed log: someone with full file access can still delete the whole file,
/// which Verify cannot prove. Nothing leaves the PC. Details are short and never hold secrets or message content.
/// </summary>
public sealed class AuditLogService
{
    internal const int MaximumFieldLength = 200;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();
    private readonly string _path;

    public AuditLogService() : this(Path.Combine(ServerState.StateDirectory, "audit-log.jsonl")) { }

    public AuditLogService(string path) => _path = path;

    /// <summary>Records an action in the default log. A failure to write is swallowed: the audit record never decides whether the action itself works.</summary>
    public static void TryRecord(string action, string subject, string? detail = null)
    {
        try { new AuditLogService().Append(action, subject, detail); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Raised after an entry is written, so optional listeners (webhooks) can react. A listener's failure never reaches the caller.</summary>
    public static event Action<AuditEntry>? Appended;

    public AuditEntry Append(string action, string subject, string? detail = null)
    {
        lock (Gate)
        {
            IReadOnlyList<AuditEntry> existing = ReadAll();
            AuditEntry? last = existing.Count > 0 ? existing[^1] : null;
            var entry = new AuditEntry
            {
                Sequence = (last?.Sequence ?? 0) + 1,
                TimestampUtc = DateTime.UtcNow,
                Action = Clip(action),
                Subject = Clip(subject),
                Detail = Clip(detail ?? string.Empty),
                PreviousHash = last?.Hash ?? string.Empty,
            };
            entry.Hash = ComputeHash(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n", Encoding.UTF8);
            try { Appended?.Invoke(entry); } catch (Exception) { }
            return entry;
        }
    }

    /// <summary>The newest entries first, at most <paramref name="limit"/> (1-500).</summary>
    public IReadOnlyList<AuditEntry> Recent(int limit = 50)
    {
        int take = Math.Clamp(limit, 1, 500);
        lock (Gate) return ReadAll().Reverse().Take(take).ToList();
    }

    public AuditVerification Verify()
    {
        lock (Gate)
        {
            IReadOnlyList<AuditEntry> all = ReadAll(out int unreadable);
            string previous = string.Empty;
            long expected = 1;
            foreach (AuditEntry entry in all)
            {
                if (entry.Sequence != expected || entry.PreviousHash != previous || entry.Hash != ComputeHash(entry))
                    return new AuditVerification
                    {
                        Intact = false, Entries = all.Count, FirstBrokenSequence = entry.Sequence,
                        Message = "The audit log was changed at entry " + entry.Sequence + ".",
                    };
                previous = entry.Hash;
                expected++;
            }
            if (unreadable > 0)
                return new AuditVerification { Intact = false, Entries = all.Count, Message = unreadable + " line(s) of the audit log cannot be read." };
            return new AuditVerification { Intact = true, Entries = all.Count, Message = all.Count == 0 ? "The audit log is empty." : "The audit log is intact." };
        }
    }

    internal static string ComputeHash(AuditEntry e)
    {
        string payload = string.Join("\n", e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            e.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), e.Action, e.Subject, e.Detail, e.PreviousHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string Clip(string value)
    {
        string flat = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= MaximumFieldLength ? flat : flat[..MaximumFieldLength];
    }

    private IReadOnlyList<AuditEntry> ReadAll() => ReadAll(out _);

    private IReadOnlyList<AuditEntry> ReadAll(out int unreadable)
    {
        unreadable = 0;
        var list = new List<AuditEntry>();
        if (!File.Exists(_path)) return list;
        foreach (string line in File.ReadLines(_path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                AuditEntry? entry = JsonSerializer.Deserialize<AuditEntry>(line, Json);
                if (entry is null) unreadable++; else list.Add(entry);
            }
            catch (JsonException) { unreadable++; }
        }
        return list;
    }
}
