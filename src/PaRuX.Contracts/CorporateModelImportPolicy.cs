namespace PaRuX.Contracts;

/// <summary>Initial Corporate file-picker validation. The existing Core importer checks
/// destination paths and duplicate names; disk exhaustion and native model compatibility must
/// still be handled and verified on a real Windows machine. A .gguf extension is not proof of
/// a valid or hardware-compatible model.</summary>
public static class CorporateModelImportPolicy
{
    /// <summary>Checks only the four-byte GGUF signature without reading a
    /// potentially multi-GB model into memory. This is a cheap preflight, not a
    /// full model validation; llama.cpp still verifies its contents at load.</summary>
    public static bool HasGgufHeader(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) == 4
                && header[0] == (byte)'G' && header[1] == (byte)'G'
                && header[2] == (byte)'U' && header[3] == (byte)'F';
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsAcceptableModel(string? fileName, long bytes)
    {
        if (bytes < 64 * 1024 || string.IsNullOrWhiteSpace(fileName)) return false;
        string name = fileName.Trim();
        return name.Length <= 240
            && name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("projector", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("..", StringComparison.Ordinal)
            && name == Path.GetFileName(name);
    }
}
