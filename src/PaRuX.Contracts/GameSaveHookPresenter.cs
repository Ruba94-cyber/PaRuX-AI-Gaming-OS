using System.Globalization;

namespace PaRuX.Contracts;

/// <summary>
/// Wording and form rules for the save-restore confirmation and the per-game launch helpers. Pure, so the exact claims the UI
/// makes ("nothing is deleted", "a backup of the current files is made first") sit next to tests instead of inside XAML code.
/// </summary>
public static class GameSaveHookPresenter
{
    public const int PreviewNames = 8;

    /// <summary>Counts by action plus a short list of the first affected file names. Names are file names inside the target only.</summary>
    public static (int Create, int Overwrite, int Unchanged, IReadOnlyList<string> Preview) Summarize(GameSaveRestorePlanDto plan)
    {
        int create = plan.Items.Count(i => i.Action == "create");
        int overwrite = plan.Items.Count(i => i.Action == "overwrite");
        int unchanged = plan.Items.Count(i => i.Action == "unchanged");
        var preview = plan.Items.Where(i => i.Action is "create" or "overwrite").Take(PreviewNames)
            .Select(i => (i.Action == "overwrite" ? "~ " : "+ ") + i.Name).ToList();
        return (create, overwrite, unchanged, preview);
    }

    /// <summary>Body of the confirmation dialog. Only called for a plan the Core accepted (<see cref="GameSaveRestorePlanDto.Ok"/>).</summary>
    public static string ConfirmationText(GameSaveRestorePlanDto plan, bool italian)
    {
        var (create, overwrite, unchanged, preview) = Summarize(plan);
        string counts = italian
            ? $"{create} file nuovi, {overwrite} sostituiti, {unchanged} già identici."
            : $"{create} new files, {overwrite} replaced, {unchanged} already identical.";
        string safety = italian
            ? "Prima di scrivere, PaRuX salva automaticamente una copia dei file attuali nella cartella di destinazione. I file che non sono nel backup non vengono mai eliminati. Dopo il ripristino ogni file viene ricontrollato con SHA-256."
            : "Before writing, PaRuX automatically saves a copy of the files currently in the target folder. Files that are not in the backup are never deleted. After the restore every file is checked again with SHA-256.";
        string list = preview.Count == 0 ? string.Empty : "\n\n" + string.Join("\n", preview) + (create + overwrite > preview.Count ? "\n…" : string.Empty);
        return counts + "\n\n" + safety + list;
    }

    public static string RestoreOutcome(GameSaveRestoreResultDto result, bool italian)
    {
        if (!result.Success)
            return italian
                ? "Ripristino non riuscito: " + result.Message + " I file originali sono stati lasciati come prima o ripristinati dalla copia automatica."
                : "Restore failed: " + result.Message + " The original files were left as they were or put back from the automatic copy.";
        string safety = result.PreRestoreBackupId is null
            ? italian ? "La cartella era vuota, nessuna copia preventiva necessaria." : "The folder was empty, so no pre-restore copy was needed."
            : italian ? "Copia dei file precedenti salvata tra i backup." : "A copy of the previous files was saved among the backups.";
        return (italian ? $"Ripristinati {result.FilesWritten} file e verificati. " : $"Restored {result.FilesWritten} files and verified them. ") + safety;
    }

    /// <summary>The label a per-game backup carries, so one game's list shows only its own copies.</summary>
    public static string BackupLabel(string gameName)
    {
        string clean = new string((gameName ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) clean = "Game";
        return clean.Length > 100 ? clean[..100] : clean;
    }

    public static bool BelongsToGame(GameSaveBackupDto backup, string gameName) =>
        string.Equals(backup.Label, BackupLabel(gameName), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Builds one hook phase from the form. Empty path = phase unset (null, no error). Returns an error text, or null when valid.
    /// The Core validates the executable again (existence, link/network/safety policy); this only rejects what can be told from the form.
    /// </summary>
    public static (GameHookSpec? Spec, string? Error) ParseHookForm(string? path, string? arguments, string? timeoutText, bool italian)
    {
        string p = (path ?? string.Empty).Trim().Trim('"');
        if (p.Length == 0) return (null, null);
        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (null, italian ? "Un helper deve essere un file .exe (niente script o collegamenti)." : "A helper must be an .exe file (no scripts or shortcuts).");
        string args = arguments ?? string.Empty;
        if (args.Length > 1024 || args.Any(char.IsControl))
            return (null, italian ? "Gli argomenti possono avere al massimo 1024 caratteri, senza caratteri di controllo." : "Arguments can have at most 1024 characters, without control characters.");
        int timeout = 15;
        if (!string.IsNullOrWhiteSpace(timeoutText) && !int.TryParse(timeoutText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out timeout))
            return (null, italian ? "Il timeout deve essere un numero di secondi da 1 a 60." : "The timeout must be a number of seconds from 1 to 60.");
        if (timeout is < 1 or > 60)
            return (null, italian ? "Il timeout deve essere da 1 a 60 secondi." : "The timeout must be 1 to 60 seconds.");
        return (new GameHookSpec(p, args, timeout), null);
    }

    public static string HookConfirmationText(GameHooksDto hooks, bool italian)
    {
        var lines = new List<string>();
        if (hooks.Pre is { } pre)
            lines.Add((italian ? "Prima dell'avvio: " : "Before launch: ") + pre.Path + (pre.Arguments.Length > 0 ? " " + pre.Arguments : "") + $" ({pre.TimeoutSeconds} s)");
        if (hooks.Post is { } post)
            lines.Add((italian ? "Dopo la chiusura: " : "After the game closes: ") + post.Path + (post.Arguments.Length > 0 ? " " + post.Arguments : "") + $" ({post.TimeoutSeconds} s)");
        string rule = italian
            ? "Il programma gira con i tuoi permessi normali (mai come amministratore), viene interrotto allo scadere del timeout e non parte mai mentre è attiva la modalità Gaming. Un errore dell'helper non blocca l'avvio del gioco."
            : "The program runs with your normal rights (never as administrator), is stopped when the timeout ends, and never starts while Gaming is active. A helper failure never blocks the game from launching.";
        return string.Join("\n", lines) + "\n\n" + rule;
    }
}
