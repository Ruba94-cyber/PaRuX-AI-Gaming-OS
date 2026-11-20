namespace PaRuX.Contracts;

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
