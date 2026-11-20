using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class AuditLogServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-audit-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "audit-log.jsonl");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Entries_chain_together_and_the_log_verifies()
    {
        var log = new AuditLogService(FilePath);
        Assert.True(log.Verify().Intact);
        AuditEntry first = log.Append("plugin.install", "demo 1.0.0", "unsigned");
        AuditEntry second = log.Append("plugin.uninstall", "demo 1.0.0");

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.Hash, second.PreviousHash);
        AuditVerification check = log.Verify();
        Assert.True(check.Intact);
        Assert.Equal(2, check.Entries);
        Assert.Equal("plugin.uninstall", log.Recent(1)[0].Action);
    }

    [Fact]
    public void Editing_a_line_is_detected_at_that_entry()
    {
        var log = new AuditLogService(FilePath);
        log.Append("a", "one");
        log.Append("b", "two");
        log.Append("c", "three");
        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("\"two\"", "\"2\""));

        AuditVerification check = log.Verify();
        Assert.False(check.Intact);
        Assert.Equal(2, check.FirstBrokenSequence);
    }

    [Fact]
    public void Deleting_a_middle_line_is_detected()
    {
        var log = new AuditLogService(FilePath);
        log.Append("a", "one");
        log.Append("b", "two");
        log.Append("c", "three");
        string[] lines = File.ReadAllLines(FilePath);
        File.WriteAllLines(FilePath, new[] { lines[0], lines[2] });

        Assert.False(log.Verify().Intact);
    }

    [Fact]
    public void A_damaged_line_is_reported_and_fields_are_single_line_and_bounded()
    {
        var log = new AuditLogService(FilePath);
        AuditEntry entry = log.Append("x", "line one\nline two", new string('d', 1000));
        Assert.DoesNotContain('\n', entry.Subject);
        Assert.Equal(AuditLogService.MaximumFieldLength, entry.Detail.Length);
        File.AppendAllText(FilePath, "{ not json\n");
        Assert.False(log.Verify().Intact);
        Assert.Single(log.Recent(10));
    }

    [Fact]
    public void Recent_is_newest_first_and_bounded()
    {
        var log = new AuditLogService(FilePath);
        for (int i = 0; i < 5; i++) log.Append("t", i.ToString());
        IReadOnlyList<AuditEntry> recent = log.Recent(3);
        Assert.Equal(new[] { "4", "3", "2" }, recent.Select(e => e.Subject));
        Assert.Single(log.Recent(0));
    }
}
