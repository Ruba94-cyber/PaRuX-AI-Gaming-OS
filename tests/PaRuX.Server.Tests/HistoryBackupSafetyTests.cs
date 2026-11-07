using Microsoft.Data.Sqlite;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Pure SQLite fixtures in a private temp folder: never touch a
/// real user's desktop history or restore anything implicitly.</summary>
public sealed class HistoryBackupSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-chat-history-safety-" + Guid.NewGuid().ToString("N"));

    public HistoryBackupSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Snapshot(string name, int rows)
    {
        string path = Path.Combine(_root, name);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"CREATE TABLE conversations (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            document_json TEXT NOT NULL
        );";
        cmd.ExecuteNonQuery();
        for (int i = 0; i < rows; i++)
        {
            cmd.CommandText = "INSERT INTO conversations VALUES ($id, 'Test', '2026-11-01', '2026-11-01', '{\"messages\":[]}');";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", i.ToString());
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    [Fact]
    public void Empty_and_corrupt_daily_copies_are_never_selected_for_recovery()
    {
        string older = Snapshot("history-2026-11-04.db", 3);
        string empty = Snapshot("history-2026-11-05.db", 0);
        string corrupt = Path.Combine(_root, "history-2026-11-06.db");
        File.WriteAllBytes(corrupt, new byte[] { 1, 2, 3, 4, 5 });

        Assert.True(HistoryBackupSafety.IsUsableSnapshot(older));
        Assert.False(HistoryBackupSafety.IsUsableSnapshot(empty));
        Assert.False(HistoryBackupSafety.IsUsableSnapshot(corrupt));
        Assert.Equal(older, HistoryBackupSafety.MostRecentUsableSnapshot(new[]
        {
            older, empty, corrupt
        }));
    }

    [Fact]
    public void Readiness_requires_real_rows_and_does_not_create_or_edit_a_snapshot()
    {
        string one = Snapshot("history-2026-11-01.db", 1);
        string before = File.ReadAllBytes(one);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = one, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        conn.Open();
        Assert.Equal(1L, HistoryBackupSafety.CountRows(conn));
        Assert.True(HistoryBackupSafety.IsUsableSnapshot(one));
        Assert.Equal(before, File.ReadAllBytes(one));
        Assert.Null(HistoryBackupSafety.MostRecentUsableSnapshot(Array.Empty<string>()));
    }

    [Fact]
    public void Promotion_never_overwrites_an_existing_healthy_chat_archive()
    {
        string healthy = Snapshot("history-2026-11-07.db", 3);
        string staging = Snapshot("history-2026-11-07.db.pending-new", 1);
        byte[] original = File.ReadAllBytes(healthy);

        Assert.False(HistoryBackupSafety.PromoteSnapshot(staging, healthy));
        Assert.Equal(original, File.ReadAllBytes(healthy));
        Assert.True(File.Exists(staging)); // caller decides whether to discard
    }

    [Fact]
    public void Promotion_preserves_the_invalid_prior_file_and_rejects_empty_candidates()
    {
        string daily = Snapshot("history-2026-11-07.db", 0);
        byte[] previous = File.ReadAllBytes(daily);
        string pending = Snapshot("history-2026-11-07.db.pending-good", 2);
        Assert.True(HistoryBackupSafety.PromoteSnapshot(pending, daily));
        Assert.True(HistoryBackupSafety.IsUsableSnapshot(daily));
        Assert.False(File.Exists(pending));

        string[] preserved = Directory.GetFiles(_root,
            "history-2026-11-07.db.previous-*.db");
        Assert.Single(preserved);
        Assert.Equal(previous, File.ReadAllBytes(preserved[0]));

        string invalid = Snapshot("history-2026-11-07.db.pending-empty", 0);
        Assert.Throws<IOException>(() => HistoryBackupSafety.PromoteSnapshot(invalid, daily));
        Assert.True(HistoryBackupSafety.IsUsableSnapshot(daily));
    }

    [Fact]
    public void A_newer_verified_nonempty_backup_wins_over_an_older_one()
    {
        string older = Snapshot("history-2026-10-31.db", 2);
        string newer = Snapshot("history-2026-11-02.db", 1);
        Assert.Equal(newer, HistoryBackupSafety.MostRecentUsableSnapshot(new[]
        {
            older, newer
        }));
    }
}
