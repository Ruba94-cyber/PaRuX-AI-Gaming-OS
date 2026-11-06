using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local, explicit-column training recipes using the existing Dataset Hub parsers.
/// No new runtime, external service, model download, synthetic label generation or
/// mutations to user-owned input files. Output lives only under PaRuX-managed data.
/// </summary>
public sealed partial class DatasetCurationService
{
    private const int RecipeMaximumRows = 20_000;
    private const long RecipeMaximumSourceBytes = 64L * 1024 * 1024;
    private const long RecipeMaximumOutputBytes = 32L * 1024 * 1024;
    private sealed record TrainingRecipeTurn(string role, string content);

    // Hermetic tests must not write their synthetic training files into the user's
    // actual PaRuXDataRoot. Production always uses the managed data directory.
    internal string? TrainingRecipeTestOutputRoot { get; set; }

    public async Task<TrainingDatasetRecipeResult> CreateTrainingRecipeAsync(
        TrainingDatasetRecipeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string method = TrainingMethod.Normalize(request.Method);
        if (method is not (TrainingMethod.Sft or TrainingMethod.Dpo or TrainingMethod.Kto))
            throw new InvalidDataException("Column mapping supports only SFT, DPO and KTO. Other methods need their own verified recipe.");

        if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
            throw new FileNotFoundException("Select a real local dataset file before building a training recipe.", request.SourcePath);
        string input = Path.GetFullPath(request.SourcePath);
        if (new FileInfo(input).Length > RecipeMaximumSourceBytes)
            throw new InvalidDataException("Dataset recipe inputs are limited to 64 MiB. Split the dataset first.");
        string format = DetectFormat(input);
        if (format == "unsupported")
            throw new InvalidDataException("Training recipes accept CSV, JSON, JSONL and Parquet files.");

        string promptColumn = request.PromptColumn?.Trim() ?? string.Empty;
        string responseColumn = request.ResponseColumn?.Trim() ?? string.Empty;
        string extraColumn = request.ExtraColumn?.Trim() ?? string.Empty;
        string systemColumn = request.SystemColumn?.Trim() ?? string.Empty;
        if (promptColumn.Length == 0 || responseColumn.Length == 0
            || promptColumn == responseColumn)
            throw new InvalidDataException("Choose two different, explicit prompt and response columns.");
        if (method != TrainingMethod.Sft && systemColumn.Length > 0)
            throw new InvalidDataException("System messages are supported only by the chat SFT recipe.");
        if ((method is TrainingMethod.Dpo or TrainingMethod.Kto)
            && (extraColumn.Length == 0 || extraColumn == promptColumn || extraColumn == responseColumn))
            throw new InvalidDataException("DPO/KTO needs a separate, explicitly mapped rejected/label column.");

        List<Dictionary<string, string?>> rows = await ReadRowsAsync(input, format, ct).ConfigureAwait(false);
        if (rows.Count == 0 || rows.Count >= MaxRowsToInspect || rows.Count > RecipeMaximumRows)
            throw new InvalidDataException("Dataset recipe must contain 1 to 20,000 complete source rows. Split larger sources first.");

        HashSet<string> columns = rows.SelectMany(row => row.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (string key in new[] { promptColumn, responseColumn, extraColumn, systemColumn }.Where(x => x.Length > 0))
            if (!columns.Contains(key))
                throw new InvalidDataException("Mapped dataset column '" + key + "' does not exist. Inspect first and choose an exact column name.");

        var lines = new List<string>(rows.Count);
        int skipped = 0, positive = 0, negative = 0;
        long outputBytes = 0;
        foreach (Dictionary<string, string?> row in rows)
        {
            ct.ThrowIfCancellationRequested();
            string? prompt = row.GetValueOrDefault(promptColumn)?.Trim();
            string? response = row.GetValueOrDefault(responseColumn)?.Trim();
            string? extra = extraColumn.Length > 0 ? row.GetValueOrDefault(extraColumn)?.Trim() : null;
            string? system = systemColumn.Length > 0 ? row.GetValueOrDefault(systemColumn)?.Trim() : null;
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(response)
                || (method != TrainingMethod.Sft && string.IsNullOrWhiteSpace(extra)))
            {
                skipped++;
                continue;
            }

            string line;
            if (method == TrainingMethod.Sft)
            {
                var messages = new List<TrainingRecipeTurn>(3);
                if (!string.IsNullOrWhiteSpace(system))
                    messages.Add(new TrainingRecipeTurn("system", system));
                messages.Add(new TrainingRecipeTurn("user", prompt));
                messages.Add(new TrainingRecipeTurn("assistant", response));
                line = JsonSerializer.Serialize(new { messages });
            }
            else if (method == TrainingMethod.Dpo)
            {
                if (string.Equals(response, extra, StringComparison.Ordinal))
                    throw new InvalidDataException("Chosen and rejected responses are identical; DPO needs real distinct feedback pairs.");
                line = JsonSerializer.Serialize(new { prompt, chosen = response, rejected = extra });
            }
            else
            {
                if (!bool.TryParse(extra, out bool desirable))
                    throw new InvalidDataException("KTO label must contain the literal true/false value, not a guessed or numeric class.");
                if (desirable) positive++; else negative++;
                line = JsonSerializer.Serialize(new { prompt, completion = response, label = desirable });
            }
            outputBytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (outputBytes > RecipeMaximumOutputBytes)
                throw new InvalidDataException("Recipe output exceeded 32 MiB. Reduce source size or split first.");
            lines.Add(line);
        }

        if (lines.Count == 0 || (method != TrainingMethod.Sft && lines.Count < 4))
            throw new InvalidDataException("Too few complete mapped records for the selected training method.");
        if (skipped > rows.Count / 20)
            throw new InvalidDataException("More than 5% of source records are incomplete. Clean the dataset before training; no data was written.");
        if (method == TrainingMethod.Kto && (positive == 0 || negative == 0))
            throw new InvalidDataException("KTO requires real positive and negative labels. No dataset was written.");

        // The explicit, reviewed result is created inside PaRuX-owned storage, never
        // beside the user's document. A partial write is not a successful recipe.
        string root = TrainingRecipeTestOutputRoot
            ?? Path.Combine(PaRuXDataRoot.Path, "datasets", "training-recipes");
        Directory.CreateDirectory(root);
        string name = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(root, name + "." + method + ".jsonl");
        string staging = destination + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(staging, lines, new UTF8Encoding(false), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string hash;
            await using (FileStream stream = File.OpenRead(staging))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
            ct.ThrowIfCancellationRequested();
            File.Move(staging, destination);
            return new TrainingDatasetRecipeResult
            {
                TrainJsonlPath = destination,
                Method = method,
                SourceRows = rows.Count,
                WrittenRows = lines.Count,
                SkippedRows = skipped,
                Sha256 = hash,
                Note = "PaRuX-owned local JSONL. No labels, answers or rejected completions were synthesized."
            };
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
