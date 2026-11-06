namespace PaRuX.Contracts;

/// <summary>Initial Corporate file-picker validation. The existing Core importer checks
/// destination paths and duplicate names; disk exhaustion and native model compatibility must
/// still be handled and verified on a real Windows machine. A .gguf extension is not proof of
/// a valid or hardware-compatible model.</summary>
public static class CorporateModelImportPolicy
{
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
