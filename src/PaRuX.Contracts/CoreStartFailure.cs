using System;
using System.IO;
using System.Security;

namespace PaRuX.Contracts;

/// <summary>Per-launch, best-effort diagnostics between Desktop and its Core child.</summary>
public static class CoreStartFailure
{
    public const string AttemptEnvironmentVariable = "PARUX_CORE_START_ATTEMPT";

    private static string? PathFor(string root, string? attempt)
    {
        if (!Guid.TryParseExact(attempt, "N", out Guid id)) return null;
        return Path.Combine(root, "core-start." + id.ToString("N") + ".error.txt");
    }

    // Refuse diagnostics through a linked data root or ancestor. This is a best-effort
    // preflight, not a substitute for handle-based protection against path replacement.
    private static bool HasSafePath(string root, string path)
    {
        var directory = new DirectoryInfo(root);
        if (!directory.Exists) return false;
        for (DirectoryInfo? current = directory; current != null; current = current.Parent)
        {
            if (current.LinkTarget != null) return false;
        }
        return new FileInfo(path).LinkTarget == null;
    }

    public static void TryWrite(string root, string? attempt, string message)
    {
        string? path = PathFor(root, attempt);
        if (path == null) return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!HasSafePath(root, path)) return;
            File.WriteAllText(temporary, message);
            File.Move(temporary, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        finally
        {
            try { if (HasSafePath(root, path)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }

    public static string? TryRead(string root, string? attempt)
    {
        string? path = PathFor(root, attempt);
        if (path == null) return null;
        try
        {
            if (!HasSafePath(root, path)) return null;
            using var reader = new StreamReader(path);
            char[] buffer = new char[400];
            int count = reader.ReadBlock(buffer, 0, buffer.Length);
            string value = new string(buffer, 0, count).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (SecurityException) { return null; }
    }

    public static void TryDelete(string root, string? attempt)
    {
        string? path = PathFor(root, attempt);
        if (path == null) return;
        try { if (HasSafePath(root, path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
    }
}
