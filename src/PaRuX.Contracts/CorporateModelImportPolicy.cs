namespace PaRuX.Contracts;

/// <summary>Initial Corporate file-picker validation. The Core still verifies destination path,
/// duplicate names, disk space and actual loader compatibility; an extension alone is not proof
/// that a file contains a usable GGUF model.</summary>
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
