using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingSessionRecordTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(),
        "parux-session-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Save_then_load_round_trips_and_delete_removes_it()
    {
        string path = TempFile();
        try
        {
            var record = new GamingSessionRecord("preset-remote-sunshine", "sunshine", "sunshine", "steam",
                new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
            GamingSessionRecord.Save(record, path);
            Assert.Equal(record, GamingSessionRecord.Load(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
            GamingSessionRecord.Delete(path);
            Assert.Null(GamingSessionRecord.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Corrupt_or_oversized_record_is_ignored_not_thrown()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(GamingSessionRecord.Load(path));
            File.WriteAllText(path, new string('x', 32 * 1024));
            Assert.Null(GamingSessionRecord.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_record_loads_as_null_and_delete_is_idempotent()
    {
        string path = TempFile();
        Assert.Null(GamingSessionRecord.Load(path));
        GamingSessionRecord.Delete(path);
    }
}
