using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Puts the PaRuX XInput shim next to ONE game, on the owner's explicit request, and takes it out again. This is the driverless
/// replacement for ViGEmBus + HidHide for games that read XInput: the game loads xinput1_3/1_4/9_1_0 from its own folder, and the shim
/// serves the pad PaRuX publishes (and forwards to Windows when PaRuX is not running). Rules this class keeps:
/// it only ever writes the three shim names inside the chosen game folder; it never overwrites a file it did not put there (another
/// xinput DLL, for example x360ce, is a conflict, not something to replace); it refuses folders that hold a known anti-cheat, because
/// those flag proxy DLLs; it records the SHA-256 of what it wrote and removes a file only while it is still byte-identical to that.
/// </summary>
public sealed class XInputShimInstaller
{
    public static readonly string[] ShimNames = { "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll" };
    private static readonly string[] AntiCheatMarkers =
    {
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService.exe", "BEService_x64.exe", "BattlEye", "vgk.sys", "EAAntiCheat.GameService.exe",
        "PnkBstrA.exe", "PnkBstrB.exe", "XIGNCODE", "nProtect", "GameGuard",
    };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private readonly string _shimRoot;
    private readonly string _recordPath;

    /// <param name="shimRoot">Folder holding x64\parux_xinput.dll and x86\parux_xinput.dll.</param>
    /// <param name="stateDirectory">Where the record of installs is kept (never inside a game folder).</param>
    public XInputShimInstaller(string shimRoot, string stateDirectory)
    {
        _shimRoot = Path.GetFullPath(shimRoot);
        _recordPath = Path.Combine(Path.GetFullPath(stateDirectory), "xinput-shim-installs.json");
    }

    public bool Available => File.Exists(SourceFor("x64")) || File.Exists(SourceFor("x86"));

    private string SourceFor(string architecture) => Path.Combine(_shimRoot, architecture, "parux_xinput.dll");

    public IReadOnlyList<XInputShimInstall> List() { lock (Gate) return Read(); }

    /// <summary>"x64", "x86" or null: the machine type of the largest executable at the top of the folder, read from its PE header only.</summary>
    public static string? DetectArchitecture(string folder)
    {
        try
        {
            string? exe = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
            if (exe is null) return null;
            using var s = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var dos = new byte[64];
            if (s.Read(dos, 0, 64) < 64 || dos[0] != 'M' || dos[1] != 'Z') return null;
            int pe = BitConverter.ToInt32(dos, 0x3C);
            if (pe < 64 || pe > 4096) return null;
            s.Position = pe;
            var head = new byte[6];
            if (s.Read(head, 0, 6) < 6 || head[0] != 'P' || head[1] != 'E') return null;
            ushort machine = BitConverter.ToUInt16(head, 4);
            return machine switch { 0x8664 => "x64", 0x014C => "x86", _ => null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public XInputShimResult Install(string? folder)
    {
        string? why = ValidateFolder(folder, out string full);
        if (why is not null) return new(false, why);
        string? arch = DetectArchitecture(full);
        if (arch is null) return new(false, "Could not tell whether this game is 32-bit or 64-bit: no program (.exe) with a valid header sits at the top of the folder.");
        string source = SourceFor(arch);
        if (!File.Exists(source)) return new(false, "The " + arch + " shim is not part of this PaRuX build.");
        foreach (string marker in AntiCheatMarkers)
            if (Directory.Exists(Path.Combine(full, marker)) || File.Exists(Path.Combine(full, marker)))
                return new(false, "This game uses an anti-cheat (" + marker + "). Anti-cheat software treats a replaced XInput file as tampering and can ban the account. Nothing was changed.");

        byte[] bytes = File.ReadAllBytes(source);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        lock (Gate)
        {
            List<XInputShimInstall> all = Read();
            if (all.Any(i => string.Equals(i.Folder, full, StringComparison.OrdinalIgnoreCase)))
                return new(false, "The shim is already installed for this game.");
            foreach (string name in ShimNames)
            {
                string target = Path.Combine(full, name);
                if (File.Exists(target) || Directory.Exists(target))
                    return new(false, name + " already exists in this game's folder (another controller tool such as x360ce may use it). PaRuX does not replace files it did not put there. Nothing was changed.");
            }
            var written = new List<string>();
            try
            {
                foreach (string name in ShimNames)
                {
                    string target = Path.Combine(full, name);
                    using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.Write(bytes);
                    written.Add(target);
                    if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).Equals(hash, StringComparison.Ordinal))
                        throw new IOException("The copy does not match the shim.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (string f in written) try { File.Delete(f); } catch (Exception) { }
                return new(false, "The game folder is not writable or the copy failed (" + ex.Message + "). Nothing was changed.");
            }
            var install = new XInputShimInstall(full, arch, ShimNames, hash, DateTime.UtcNow);
            all.Add(install);
            if (!Write(all))
            {
                foreach (string f in written) try { File.Delete(f); } catch (Exception) { }
                return new(false, "The install could not be recorded, so it was undone.");
            }
            return new(true, "Installed the " + arch + " XInput shim next to this game. While PaRuX runs, the game sees the pad PaRuX publishes; otherwise it sees Windows' own controllers.", install);
        }
    }

    public XInputShimResult Uninstall(string? folder)
    {
        string? why = ValidateFolder(folder, out string full);
        if (why is not null) return new(false, why);
        lock (Gate)
        {
            List<XInputShimInstall> all = Read();
            XInputShimInstall? install = all.FirstOrDefault(i => string.Equals(i.Folder, full, StringComparison.OrdinalIgnoreCase));
            if (install is null) return new(false, "PaRuX did not install the shim for this folder, so it removes nothing there.");
            var kept = new List<string>();
            foreach (string name in install.Files)
            {
                string target = Path.Combine(full, name);
                if (!File.Exists(target)) continue;
                bool same;
                try { same = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).Equals(install.Sha256, StringComparison.Ordinal); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { same = false; }
                if (!same) { kept.Add(name); continue; }   // the owner or another tool changed it: not ours to delete any more
                try { File.Delete(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept.Add(name); }
            }
            all.RemoveAll(i => string.Equals(i.Folder, full, StringComparison.OrdinalIgnoreCase));
            Write(all);
            return kept.Count == 0
                ? new(true, "Removed the shim. The game is back to its original files.")
                : new(true, "Removed what PaRuX installed. These files were left because they no longer match what PaRuX wrote: " + string.Join(", ", kept) + ".");
        }
    }

    private static string? ValidateFolder(string? folder, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 1024 || folder.Any(char.IsControl)
            || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            return "Choose an absolute folder on a local drive.";
        full = Path.GetFullPath(folder).TrimEnd('\\');
        if (!Directory.Exists(full)) return "The folder does not exist.";
        if (!ManualGameLibrary.IsSafeDirectory(full)) return "Linked folders and folders under a link are not used.";
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
        bool protectedPath = full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)
            || full.Equals(pf, StringComparison.OrdinalIgnoreCase) || full.Equals(pf86, StringComparison.OrdinalIgnoreCase)
            || Path.GetPathRoot(full)!.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase);
        return protectedPath ? "That is a system folder. Choose the folder of one game." : null;
    }

    private List<XInputShimInstall> Read()
    {
        try { return File.Exists(_recordPath) ? JsonSerializer.Deserialize<List<XInputShimInstall>>(File.ReadAllText(_recordPath), Json) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    private bool Write(List<XInputShimInstall> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
            string temp = _recordPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
            File.Move(temp, _recordPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
