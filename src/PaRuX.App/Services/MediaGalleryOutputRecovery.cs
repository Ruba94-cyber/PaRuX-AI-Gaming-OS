using System;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// Local-only, on-demand status for gallery outputs. Gallery indexing never invokes
    /// this policy for every artifact: it is meant for the 24 visible cards and the
    /// user's explicit Preview action. It never starts or retries a generation job.
    /// </summary>
    public enum MediaGalleryOutputState
    {
        Ready,
        Generating,
        GenerationStopped,
        Missing,
        Empty,
        Unsupported,
        Unreadable
    }

    public static class MediaGalleryOutputRecovery
    {
        public static bool IsGenerating(string? status) =>
            status is not null && (status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                || status.Equals("running", StringComparison.OrdinalIgnoreCase)
                || status.Equals("processing", StringComparison.OrdinalIgnoreCase)
                || status.Equals("cancelling", StringComparison.OrdinalIgnoreCase)
                || status.Equals("downloading", StringComparison.OrdinalIgnoreCase));

        public static bool IsGenerationStopped(string? status) =>
            status is not null && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

        public static MediaGalleryOutputState Inspect(MediaGalleryItem? item)
        {
            if (item == null) return MediaGalleryOutputState.Unsupported;

            // When Core explicitly reports a running or terminal-failed job,
            // never guess that a partially written file is ready for decoding.
            if (IsGenerating(item.Status)) return MediaGalleryOutputState.Generating;
            if (IsGenerationStopped(item.Status)) return MediaGalleryOutputState.GenerationStopped;
            if (!MediaGalleryPreviewPolicy.TryGetLocalMediaPath(item, out string full))
                return MediaGalleryOutputState.Unsupported;

            try
            {
                FileAttributes attrs = File.GetAttributes(full);
                if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return MediaGalleryOutputState.Unsupported;

                return new FileInfo(full).Length > 0
                    ? MediaGalleryOutputState.Ready
                    : MediaGalleryOutputState.Empty;
            }
            catch (FileNotFoundException) { return MediaGalleryOutputState.Missing; }
            catch (DirectoryNotFoundException) { return MediaGalleryOutputState.Missing; }
            catch (UnauthorizedAccessException) { return MediaGalleryOutputState.Unreadable; }
            catch (IOException) { return MediaGalleryOutputState.Unreadable; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return MediaGalleryOutputState.Unsupported;
            }
        }

        public static string Guidance(MediaGalleryOutputState state, bool italian) =>
            state switch
            {
                MediaGalleryOutputState.Ready => italian
                    ? "File locale presente. L'integrità dei codec non è ancora verificata."
                    : "Local file present. Codec integrity has not been verified.",
                MediaGalleryOutputState.Generating => italian
                    ? "La generazione è ancora in corso o in annullamento. Controlla lo stato del job in Create o Chat; questa anteprima non avvia altri lavori."
                    : "Generation is still running or cancelling. Check the job in Create or Chat; this preview does not start another job.",
                MediaGalleryOutputState.GenerationStopped => italian
                    ? "La generazione è stata interrotta o non è riuscita. Controlla il job originale in Create o Chat prima di un nuovo tentativo; il file parziale non viene modificato."
                    : "Generation was cancelled or failed. Check the original job in Create or Chat before trying again; any partial file is left untouched.",
                MediaGalleryOutputState.Missing => italian
                    ? "Il file non si trova più nel percorso registrato. Controlla la cartella originale oppure ripeti la generazione da Create o Chat."
                    : "The file is no longer at the recorded path. Check its original folder or start a new generation from Create or Chat.",
                MediaGalleryOutputState.Empty => italian
                    ? "Il file esiste ma è vuoto. Potrebbe essere ancora in scrittura o il salvataggio potrebbe essere fallito. Controlla il job e riprova il controllo qui."
                    : "The file exists but is empty. It may still be writing or saving may have failed. Check the job, then recheck here.",
                MediaGalleryOutputState.Unreadable => italian
                    ? "Il file non è leggibile al momento. Verifica accesso e stato del disco, poi riprova il controllo; PaRuX non modifica il file."
                    : "The file is not readable right now. Check permissions and disk status, then recheck; PaRuX does not modify the file.",
                _ => italian
                    ? "Questo riferimento non è un file multimediale locale supportato. Puoi consultare i metadati, ma PaRuX non tenterà di aprirlo."
                    : "This reference is not a supported local media file. You can inspect its metadata, but PaRuX will not attempt to open it."
            };
    }
}
