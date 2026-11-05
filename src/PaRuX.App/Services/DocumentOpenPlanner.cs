using System;
using System.Collections.Generic;
using System.IO;

namespace PaRuX.Services
{
    public enum OpenStrategy { Shell, PdfAcrobat, PdfSumatra, PdfEdge, ExcelCom, PowerPointCom }

    /// <summary>
    /// What "open at this position" will really do. <see cref="Positioned"/> is true only when the chosen mechanism can jump to the
    /// position; otherwise the file is simply opened and <see cref="Message"/> tells the person where to look. No UI types here
    /// so the decision is unit-tested.
    /// </summary>
    public sealed record OpenPlan(OpenStrategy Strategy, string FilePath, string? Executable, IReadOnlyList<string> Arguments, bool Positioned, string Message);

    public static class DocumentOpenPlanner
    {
        /// <param name="findExecutable">Resolves a viewer executable name (for example "SumatraPDF.exe") to a full path, or null.</param>
        public static OpenPlan Plan(string path, int? page, string? sheet, string? cellRange, int? slide, string? heading,
            Func<string, string?> findExecutable, bool excelAvailable, bool powerPointAvailable)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string where = Describe(page, sheet, cellRange, slide, heading);

            if (ext == ".pdf" && page.HasValue)
            {
                string? acrobat = findExecutable("Acrobat.exe") ?? findExecutable("AcroRd32.exe");
                if (acrobat != null)
                    return new(OpenStrategy.PdfAcrobat, path, acrobat, new[] { "/A", "page=" + page.Value, path }, true, "Opened in Acrobat at page " + page.Value + ".");
                string? sumatra = findExecutable("SumatraPDF.exe");
                if (sumatra != null)
                    return new(OpenStrategy.PdfSumatra, path, sumatra, new[] { "-page", page.Value.ToString(), path }, true, "Opened in SumatraPDF at page " + page.Value + ".");
                string? edge = findExecutable("msedge.exe");
                if (edge != null)
                    return new(OpenStrategy.PdfEdge, path, edge, new[] { new Uri(path).AbsoluteUri + "#page=" + page.Value }, true, "Opened in Microsoft Edge at page " + page.Value + ".");
            }
            if (ext is ".xlsx" or ".xlsm" or ".xls" or ".xlsb" && sheet != null && excelAvailable)
                return new(OpenStrategy.ExcelCom, path, null, new[] { path, sheet, cellRange ?? string.Empty }, true,
                    "Opened read-only in Excel at " + where + ".");
            if (ext is ".pptx" or ".ppt" or ".pptm" && slide.HasValue && powerPointAvailable)
                return new(OpenStrategy.PowerPointCom, path, null, new[] { path, slide.Value.ToString() }, true,
                    "Opened read-only in PowerPoint at slide " + slide.Value + ".");

            string note = where.Length == 0
                ? "Opened the file."
                : "Opened the file. This format or viewer cannot jump to the position automatically; look for: " + where + ".";
            return new(OpenStrategy.Shell, path, null, Array.Empty<string>(), false, note);
        }

        public static string Describe(int? page, string? sheet, string? cellRange, int? slide, string? heading)
        {
            var parts = new List<string>();
            if (page.HasValue) parts.Add("page " + page.Value);
            if (sheet != null) parts.Add(cellRange != null ? sheet + "!" + cellRange : sheet);
            if (slide.HasValue) parts.Add("slide " + slide.Value);
            if (!string.IsNullOrWhiteSpace(heading)) parts.Add("section \"" + heading + "\"");
            return string.Join(", ", parts);
        }

        /// <summary>PowerShell scripts for the COM strategies. Values arrive as parameters, never spliced into the text.</summary>
        public const string ExcelScript = "param($Path,$Sheet,$Cell)\n$ErrorActionPreference='Stop'\n$xl=New-Object -ComObject Excel.Application\n$xl.Visible=$true\n"
            + "$wb=$xl.Workbooks.Open($Path,0,$true)\ntry{$ws=$wb.Worksheets.Item($Sheet);$ws.Activate();if($Cell){$null=$ws.Range($Cell).Select()}}catch{}\n";
        public const string PowerPointScript = "param($Path,$Slide)\n$ErrorActionPreference='Stop'\n$pp=New-Object -ComObject PowerPoint.Application\n"
            + "$pres=$pp.Presentations.Open($Path,-1,0,-1)\ntry{$null=$pres.Windows.Item(1).View.GotoSlide([int]$Slide)}catch{}\n";
    }
}
