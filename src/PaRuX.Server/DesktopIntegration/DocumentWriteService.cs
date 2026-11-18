using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Writes Word and Excel documents PaRuX generates, into one folder the user nominated.
/// Reading and writing are separate authorities; generated documents never overwrite an existing
/// file. Native workbook edits are additionally restricted to an existing .xlsx already inside the
/// configured output folder and always produce a new file, preserving the source byte-for-byte.
/// </summary>
public sealed class DocumentWriteService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory;
    private readonly object _gate = new();
    private string? _cachedFolder;

    public DocumentWriteService() : this(ServerState.StateDirectory) { }

    public DocumentWriteService(string stateDirectory) => _stateDirectory = stateDirectory;

    private string ConfigPath => Path.Combine(_stateDirectory, "document-output.json");

    public string OutputFolder
    {
        get
        {
            lock (_gate)
            {
                if (_cachedFolder != null) return _cachedFolder;
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        Config? config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOptions);
                        if (!string.IsNullOrWhiteSpace(config?.Folder)) return _cachedFolder = config!.Folder!;
                    }
                }
                catch { }
                return _cachedFolder = Path.Combine(PaRuXDataRoot.Path, "documents");
            }
        }
    }

    public void SetOutputFolder(string? folder)
    {
        string resolved = (folder ?? string.Empty).Trim();
        if (resolved.Length == 0)
        {
            lock (_gate)
            {
                _cachedFolder = null;
                try { File.Delete(ConfigPath); } catch { }
            }
            return;
        }

        resolved = Path.GetFullPath(resolved);
        if (!Directory.Exists(resolved))
            throw new InvalidDataException("That folder does not exist: " + resolved);

        lock (_gate)
        {
            Directory.CreateDirectory(_stateDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config { Folder = resolved }, JsonOptions));
            _cachedFolder = resolved;
        }
    }

    public DocumentWriteResult WriteWord(string fileName, WordDocumentContent content)
    {
        byte[] bytes = OfficeDocumentWriter.BuildWord(content);
        return Save(fileName, ".docx", bytes);
    }

    public DocumentWriteResult WriteWorkbook(string fileName, WorkbookContent content)
    {
        StructuredWorkbookResult workbook = StructuredWorkbookWriter.Build(content);
        return SaveWorkbook(fileName, workbook, sourcePreserved: false);
    }

    /// <summary>
    /// Applies targeted workbook V2 cells/charts to an existing output workbook while preserving
    /// unrelated package content. The source must be a simple .xlsx filename in OutputFolder; this
    /// deliberately does not turn a write tool into arbitrary file-read authority.
    /// </summary>
    public DocumentWriteResult EditWorkbook(string sourceFileName, string outputFileName, WorkbookContent patch)
    {
        string sourceName = (sourceFileName ?? string.Empty).Trim();
        if (!sourceName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            || !PaRuXPathSafety.IsSimpleFileName(sourceName))
            throw new InvalidDataException("Workbook source must be a plain .xlsx filename from PaRuX's output folder.");

        string folder = OutputFolder;
        Directory.CreateDirectory(folder);
        string sourcePath = Path.GetFullPath(Path.Combine(folder, sourceName));
        string authorizedRoot = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!sourcePath.StartsWith(authorizedRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Workbook source escapes the configured output folder.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Workbook source does not exist in PaRuX's output folder.", sourcePath);

        byte[] source = File.ReadAllBytes(sourcePath);
        StructuredWorkbookResult workbook = StructuredWorkbookWriter.Patch(source, patch);
        workbook = WorkbookPatchStylePreserver.Apply(source, workbook, patch);
        return SaveWorkbook(outputFileName, workbook, sourcePreserved: true);
    }

    private DocumentWriteResult SaveWorkbook(
        string fileName,
        StructuredWorkbookResult workbook,
        bool sourcePreserved)
    {
        DocumentWriteResult result = Save(fileName, ".xlsx", workbook.Bytes);
        result.CalculationStatus = workbook.CalculationStatus;
        result.OpenXmlValidated = workbook.Validated;
        result.ValidationWarnings = workbook.ValidationWarnings.ToList();
        result.SourceWorkbookPreserved = sourcePreserved;
        return result;
    }

    private DocumentWriteResult Save(string fileName, string extension, byte[] bytes)
    {
        string trimmed = (fileName ?? string.Empty).Trim();
        string baseName = trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^extension.Length]
            : trimmed;
        if (baseName.Length == 0 || !PaRuXPathSafety.IsSimpleFileName(baseName + extension))
            throw new InvalidDataException(
                "The document name must be a plain file name with no folders in it.");

        return PublishNew(baseName, extension, bytes);
    }

    // Shared by generated documents and approved edits: never overwrite, even with concurrent writers.
    internal DocumentWriteResult PublishNew(string baseName, string extension, byte[] bytes, string? expectedFolder = null)
    {
        if (!PaRuXPathSafety.IsSimpleFileName(baseName + extension))
            throw new InvalidDataException("The document name must be a plain file name with no folders in it.");
        lock (_gate)
        {
            string folder = Path.GetFullPath(OutputFolder);
            if (expectedFolder != null && !string.Equals(folder, expectedFolder, PaRuXPathSafety.PathComparison))
                throw new InvalidOperationException("Output folder changed; propose again.");
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, ".parux-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemporary = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { ownsTemporary = true; file.Write(bytes); file.Flush(true); }
                for (int n = 1; n <= 1000; n++)
                {
                    string name = baseName + (n == 1 ? "" : " (" + n + ")") + extension;
                    string path = Path.Combine(folder, name);
                    try { File.Move(temporary, path, overwrite: false); }
                    catch (IOException) when (File.Exists(path)) { continue; }
                    ownsTemporary = false;
                    return new DocumentWriteResult { Path = path, FileName = name, SizeBytes = bytes.LongLength, RenamedToAvoidOverwrite = n != 1 };
                }
                throw new IOException("No available output filename.");
            }
            finally
            {
                if (ownsTemporary)
                    try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class Config
    {
        public string? Folder { get; set; }
    }
}
