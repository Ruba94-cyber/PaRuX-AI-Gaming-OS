using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PaRuX.Services
{
    /// <summary>
    /// Opens a search result's original file at the best position the installed software supports (PDF page via Acrobat, SumatraPDF or
    /// Edge; Excel sheet and range and PowerPoint slide through their COM automation, opened read-only), otherwise just the file.
    /// The returned text says exactly what happened; documents are never modified.
    /// </summary>
    public static class DocumentOpener
    {
        public static async Task<string> OpenAsync(string path, int? page, string? sheet, string? cellRange, int? slide, string? heading)
        {
            if (!File.Exists(path)) return "The file is no longer there: " + Path.GetFileName(path);
            OpenPlan plan = DocumentOpenPlanner.Plan(path, page, sheet, cellRange, slide, heading, FindExecutable,
                ComClassRegistered("Excel.Application"), ComClassRegistered("PowerPoint.Application"));
            try
            {
                switch (plan.Strategy)
                {
                    case OpenStrategy.ExcelCom: await RunScriptAsync(DocumentOpenPlanner.ExcelScript, plan.Arguments).ConfigureAwait(false); break;
                    case OpenStrategy.PowerPointCom: await RunScriptAsync(DocumentOpenPlanner.PowerPointScript, plan.Arguments).ConfigureAwait(false); break;
                    case OpenStrategy.Shell: Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); break;
                    default:
                        var psi = new ProcessStartInfo(plan.Executable!) { UseShellExecute = false };
                        foreach (string a in plan.Arguments) psi.ArgumentList.Add(a);
                        Process.Start(psi);
                        break;
                }
                return plan.Message;
            }
            catch (Exception ex) when (plan.Strategy != OpenStrategy.Shell)
            {
                // The positioned mechanism failed: fall back to the plain open and say so.
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception inner) { return "Could not open the file: " + inner.Message; }
                return "Opened the file; jumping to the position did not work (" + ex.GetType().Name + "). Look for: "
                    + DocumentOpenPlanner.Describe(page, sheet, cellRange, slide, heading) + ".";
            }
            catch (Exception ex) { return "Could not open the file: " + ex.Message; }
        }

        private static async Task RunScriptAsync(string script, System.Collections.Generic.IReadOnlyList<string> args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "parux-open");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "open-" + Math.Abs(script.GetHashCode()) + ".ps1");
            File.WriteAllText(file, script);
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }) psi.ArgumentList.Add(a);
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell did not start.");
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (p.ExitCode != 0) throw new InvalidOperationException("The Office automation script failed.");
        }

        private static bool ComClassRegistered(string progId)
        {
            try { using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID"); return key != null; }
            catch (Exception) { return false; }
        }

        private static string? FindExecutable(string exe)
        {
            try
            {
                using RegistryKey? app = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe)
                    ?? Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (app?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch (Exception) { /* fall through to the fixed locations */ }
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string rel in exe switch
                {
                    "msedge.exe" => new[] { @"Microsoft\Edge\Application\msedge.exe" },
                    "SumatraPDF.exe" => new[] { @"SumatraPDF\SumatraPDF.exe" },
                    "Acrobat.exe" => new[] { @"Adobe\Acrobat DC\Acrobat\Acrobat.exe" },
                    "AcroRd32.exe" => new[] { @"Adobe\Acrobat Reader DC\Reader\AcroRd32.exe" },
                    _ => Array.Empty<string>()
                })
                {
                    string full = Path.Combine(root, rel);
                    if (File.Exists(full)) return full;
                }
            }
            return null;
        }
    }
}
