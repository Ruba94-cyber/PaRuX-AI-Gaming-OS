namespace PaRuX.UITests.Infrastructure;

/// <summary>Reads a file the Core may hold open for writing (read/write/delete sharing, bounded retry).</summary>
internal static class SharedFile
{
    public static string ReadAllText(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
        }
    }
}
