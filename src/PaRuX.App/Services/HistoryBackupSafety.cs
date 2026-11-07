using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace PaRuX.Services
{
    /// <summary>
    /// Conservative daily-chat snapshot policy shared by desktop and portable.
    /// An empty or corrupt live store is NOT proof that the user intentionally
    /// deleted their history. Never use it to overwrite the last healthy
    /// non-empty snapshot. Recovery only runs after a SQLite open failure,
    /// and never merges/restores healthy live data automatically.
    /// </summary>
    public static class HistoryBackupSafety
    {
        /// <summary>The on-disk backup has a usable conversation table and real
        /// rows. The fast SQLite integrity check rejects torn/corrupt snapshots
        /// before recovery. Uses a read-only handle and does not alter the file.</summary>
        public static bool IsUsableSnapshot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
                    }.ToString());
                connection.Open();
                using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    return false;
                return CountRows(connection) > 0;
            }
            catch (Exception) { return false; }
        }

        public static long CountRows(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM conversations;";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public static string? MostRecentUsableSnapshot(IEnumerable<string> candidates) =>
            candidates.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(IsUsableSnapshot);
    }
}
