using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server.Knowledge;

/// <summary>What an OCR engine can do on this PC right now. Never guessed: the reason is shown to the person.</summary>
public sealed record OcrAvailability(bool Available, string? Reason, IReadOnlyList<string> Languages)
{
    public static OcrAvailability No(string reason) => new(false, reason, Array.Empty<string>());
}

/// <summary>Text recognised on one page (PDF) or image. <see cref="Ok"/> is false when recognition failed for that page.</summary>
public sealed record OcrPageResult(int Page, string Text, bool Ok, string? Error = null);

/// <summary>
/// Optical character recognition behind a small abstraction so the default (Windows OCR, no extra binaries) can be replaced by
/// an optional engine later. OCR is opt-in; when it is off or unavailable the file is still indexed by name and metadata and
/// the reason is recorded, never a silent empty success.
/// </summary>
public interface IOcrEngine
{
    string Name { get; }
    OcrAvailability CheckAvailability();
    /// <summary>Recognises one image file. Returns a single result with page 1.</summary>
    OcrPageResult RecognizeImage(string path, string? languageTag, CancellationToken ct);
    /// <summary>Renders and recognises the listed 1-based PDF pages.</summary>
    IReadOnlyList<OcrPageResult> RecognizePdfPages(string path, IReadOnlyList<int> pages, string? languageTag, CancellationToken ct);
}

/// <summary>Engine used when OCR is switched off or the platform has none.</summary>
public sealed class UnavailableOcrEngine : IOcrEngine
{
    private readonly string _reason;
    public UnavailableOcrEngine(string reason) => _reason = reason;
    public string Name => "none";
    public OcrAvailability CheckAvailability() => OcrAvailability.No(_reason);
    public OcrPageResult RecognizeImage(string path, string? languageTag, CancellationToken ct) => new(1, string.Empty, false, _reason);
    public IReadOnlyList<OcrPageResult> RecognizePdfPages(string path, IReadOnlyList<int> pages, string? languageTag, CancellationToken ct) =>
        pages.Select(p => new OcrPageResult(p, string.Empty, false, _reason)).ToList();
}

/// <summary>
/// Windows OCR (Windows.Media.Ocr) and PDF page rendering (Windows.Data.Pdf), both part of Windows, no model or binary to ship.
/// The Core targets plain net8.0 and cannot project WinRT directly, so recognition runs out of process in Windows PowerShell with
/// a timeout: a hostile or corrupt image can only take down that short-lived process. Paths are passed as arguments, never
/// interpolated into the script. Recognition runs fully offline.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private const string ScriptBody = """
param([string]$Mode,[string]$Path,[string]$Lang,[string]$Pages)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
function Emit($o) { [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress -Depth 4)) }
try {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
  $null = [Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime]
  $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
  $null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
  $null = [Windows.Storage.StorageFile, Windows.Foundation, ContentType=WindowsRuntime]
  $null = [Windows.Storage.Streams.RandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
  $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
      $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  $asTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
      $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
  function Await($op, $type) {
    $t = $asTaskGeneric.MakeGenericMethod($type).Invoke($null, @($op)); $null = $t.Wait(-1); $t.Result
  }
  if ($Mode -eq 'probe') {
    $langs = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
    Emit @{ available = ($langs.Count -gt 0); languages = $langs; reason = $(if ($langs.Count -eq 0) { 'No Windows OCR language pack is installed.' } else { $null }) }
    return
  }
  if ($Lang) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language($Lang))) }
  else { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
  if ($null -eq $engine) { Emit @{ error = 'No Windows OCR engine for the requested language.' }; return }
  $max = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
  function Recognize($decoder) {
    $w = [uint32]$decoder.PixelWidth; $h = [uint32]$decoder.PixelHeight
    if ($w -gt $max -or $h -gt $max) {
      $scale = [Math]::Min($max / $w, $max / $h)
      $tr = New-Object Windows.Graphics.Imaging.BitmapTransform
      $tr.ScaledWidth = [uint32][Math]::Floor($w * $scale); $tr.ScaledHeight = [uint32][Math]::Floor($h * $scale)
      $bmp = Await ($decoder.GetSoftwareBitmapAsync($decoder.BitmapPixelFormat, $decoder.BitmapAlphaMode, $tr, [Windows.Graphics.Imaging.ExifOrientationMode]::IgnoreExifOrientation, [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
    } else { $bmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap]) }
    $res = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
    $res.Text
  }
  $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
  if ($Mode -eq 'image') {
    $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    Emit @{ page = 1; text = (Recognize $decoder) }
  } elseif ($Mode -eq 'pdf') {
    $null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime]
    $pdf = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
    foreach ($n in ($Pages -split ',' | Where-Object { $_ })) {
      $i = [int]$n; $page = $null
      try {
        if ($i -lt 1 -or $i -gt $pdf.PageCount) { Emit @{ page = $i; error = 'Page out of range.' }; continue }
        $page = $pdf.GetPage([uint32]($i - 1))
        $ms = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
        $opt = New-Object Windows.Data.Pdf.PdfPageRenderOptions
        $targetW = [Math]::Min([double]$max, [Math]::Max(1400.0, [Math]::Floor($page.Size.Width * 200.0 / 72.0)))
        $opt.DestinationWidth = [uint32]$targetW
        $t = $asTaskAction.Invoke($null, @($page.RenderToStreamAsync($ms, $opt))); $null = $t.Wait(-1)
        $ms.Seek(0)
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($ms)) ([Windows.Graphics.Imaging.BitmapDecoder])
        Emit @{ page = $i; text = (Recognize $decoder) }
      } catch { Emit @{ page = $i; error = 'Page could not be rendered or recognised.' } }
      finally { if ($page) { $page.Dispose() } }
    }
  }
} catch { Emit @{ error = 'Windows OCR failed: ' + $_.Exception.GetType().Name } }
""";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _perCallTimeout;
    private OcrAvailability? _availability;

    public WindowsOcrEngine(TimeSpan? perCallTimeout = null) => _perCallTimeout = perCallTimeout ?? TimeSpan.FromMinutes(5);

    public string Name => "windows-ocr";

    public OcrAvailability CheckAvailability()
    {
        if (_availability != null) return _availability;
        if (!OperatingSystem.IsWindows()) return _availability = OcrAvailability.No("Windows OCR is only available on Windows.");
        string? ps = FindPowerShell();
        if (ps == null) return _availability = OcrAvailability.No("Windows PowerShell was not found; Windows OCR cannot be started.");
        List<JsonElement> lines = Run(ps, "probe", string.Empty, null, string.Empty, ProbeTimeout, CancellationToken.None, out string? failure);
        if (failure != null || lines.Count == 0) return _availability = OcrAvailability.No(failure ?? "Windows OCR did not answer.");
        JsonElement e = lines[0];
        if (e.TryGetProperty("error", out JsonElement err)) return _availability = OcrAvailability.No(err.GetString() ?? "Windows OCR failed.");
        var langs = new List<string>();
        if (e.TryGetProperty("languages", out JsonElement l))
        {
            if (l.ValueKind == JsonValueKind.Array) foreach (JsonElement x in l.EnumerateArray()) langs.Add(x.GetString() ?? string.Empty);
            else if (l.ValueKind == JsonValueKind.String) langs.Add(l.GetString() ?? string.Empty);
        }
        langs.RemoveAll(string.IsNullOrEmpty);
        bool ok = e.TryGetProperty("available", out JsonElement a) && a.ValueKind == JsonValueKind.True;
        string? reason = ok ? null : (e.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "No Windows OCR language pack is installed.");
        return _availability = new OcrAvailability(ok, reason, langs);
    }

    public OcrPageResult RecognizeImage(string path, string? languageTag, CancellationToken ct)
    {
        OcrAvailability availability = CheckAvailability();
        if (!availability.Available) return new OcrPageResult(1, string.Empty, false, availability.Reason);
        List<JsonElement> lines = Run(FindPowerShell()!, "image", path, languageTag, string.Empty, _perCallTimeout, ct, out string? failure);
        if (failure != null) return new OcrPageResult(1, string.Empty, false, failure);
        return lines.Select(Parse).FirstOrDefault() ?? new OcrPageResult(1, string.Empty, false, "Windows OCR returned nothing.");
    }

    public IReadOnlyList<OcrPageResult> RecognizePdfPages(string path, IReadOnlyList<int> pages, string? languageTag, CancellationToken ct)
    {
        if (pages.Count == 0) return Array.Empty<OcrPageResult>();
        OcrAvailability availability = CheckAvailability();
        if (!availability.Available) return pages.Select(p => new OcrPageResult(p, string.Empty, false, availability.Reason)).ToList();
        List<JsonElement> lines = Run(FindPowerShell()!, "pdf", path, languageTag, string.Join(",", pages), _perCallTimeout, ct, out string? failure);
        if (failure != null) return pages.Select(p => new OcrPageResult(p, string.Empty, false, failure)).ToList();
        var byPage = lines.Select(Parse).Where(r => r != null).ToDictionary(r => r!.Page, r => r!);
        return pages.Select(p => byPage.TryGetValue(p, out OcrPageResult? r) ? r : new OcrPageResult(p, string.Empty, false, "Page was not recognised.")).ToList();
    }

    private static OcrPageResult? Parse(JsonElement e)
    {
        int page = e.TryGetProperty("page", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 1;
        if (e.TryGetProperty("error", out JsonElement err)) return new OcrPageResult(page, string.Empty, false, err.GetString());
        string text = e.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
        return new OcrPageResult(page, text, true);
    }

    private static string? FindPowerShell()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string candidate = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string ScriptPath()
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ScriptBody)))[..12];
        string dir = Path.Combine(Path.GetTempPath(), "parux-ocr");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "ocr-" + hash + ".ps1");
        if (!File.Exists(path)) File.WriteAllText(path, ScriptBody, new UTF8Encoding(true));   // BOM: Windows PowerShell 5.1 reads UTF-8 only with it
        return path;
    }

    private static List<JsonElement> Run(string powershell, string mode, string path, string? lang, string pages, TimeSpan timeout,
        CancellationToken ct, out string? failure)
    {
        failure = null;
        var results = new List<JsonElement>();
        var psi = new ProcessStartInfo(powershell)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(), "-Mode", mode, "-Path", path, "-Lang", lang ?? string.Empty, "-Pages", pages })
            psi.ArgumentList.Add(a);
        using Process? process = Process.Start(psi);
        if (process == null) { failure = "Windows OCR could not be started."; return results; }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception) { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            failure = "Windows OCR timed out.";
            return results;
        }
        foreach (string line in stdout.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;
            try { using JsonDocument doc = JsonDocument.Parse(trimmed); results.Add(doc.RootElement.Clone()); }
            catch (JsonException) { /* not a result line */ }
        }
        if (results.Count == 0) failure = "Windows OCR returned no result.";
        return results;
    }
}

/// <summary>Image formats Windows OCR can decode. Other image types stay metadata-only.</summary>
internal sealed class ImageOcrExtractor : IDocumentExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    public string Name => "image-ocr";
    public bool CanHandle(string path) => Extensions.Contains(Path.GetExtension(path));

    public ExtractedDocument Extract(string path, ExtractionLimits limits, CancellationToken ct)
    {
        IOcrEngine? engine = limits.Ocr;
        if (engine == null)
            return new ExtractedDocument(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false,
                "Image; OCR is off (opt-in in the knowledge settings), so only the file name and metadata are searchable.", Name);
        OcrAvailability availability = engine.CheckAvailability();
        if (!availability.Available)
            return new ExtractedDocument(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false,
                "Image; OCR is unavailable (" + (availability.Reason ?? "unknown reason") + "), so only the file name and metadata are searchable.", Name);
        OcrPageResult r = engine.RecognizeImage(path, limits.OcrLanguage, ct);
        if (!r.Ok)
            return new ExtractedDocument(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false,
                "Image; OCR failed (" + (r.Error ?? "unknown reason") + "); only the file name and metadata are searchable.", Name);
        string text = r.Text.Trim();
        if (text.Length == 0)
            return new ExtractedDocument(path, DocumentTier.MetadataOnly, Array.Empty<DocumentSegment>(), false,
                "Image; OCR found no text, so only the file name and metadata are searchable.", Name);
        if (text.Length > limits.MaxCharacters) text = text[..(int)limits.MaxCharacters];
        return new ExtractedDocument(path, DocumentTier.Structured,
            new[] { new DocumentSegment(text, new DocumentPosition(Heading: "image text (OCR)")) }, false, null, Name);
    }
}
