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

        /// <summary>
        /// Promotes a SQLite-validated .pending backup without ever destroying
        /// a healthy existing snapshot. Even an invalid/empty existing file is
        /// archived rather than silently overwritten; the owner can inspect
        /// or discard it later. Caller creates staging in the same directory.
        /// </summary>
        public static bool PromoteSnapshot(string staging, string destination)
        {
            if (!IsUsableSnapshot(staging))
                throw new IOException("The pending chat backup is unreadable or empty.");
            if (IsUsableSnapshot(destination)) return false;
            if (File.Exists(destination))
            {
                string preserved = destination + ".previous-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-"
                    + Guid.NewGuid().ToString("N") + ".db";
                File.Move(destination, preserved);
            }
            File.Move(staging, destination);
            return true;
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
