using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Intelligence;

/// <summary>
/// Bounded, full-file validation of owner-supplied SFT/DPO JSONL before a Quick
/// fine-tune. Unlike the 2,000-row planner preview, every nonempty row is read.
/// The returned hash binds the checked bytes to the eventual server admission.
/// </summary>
public static class QuickTrainingDatasetValidator
{
    public const long MaximumBytes = 256L * 1024 * 1024;
    public const int MaximumRecords = 200_000;
    public const int MaximumCharactersPerRecord = 1_000_000;

    public sealed record VerifiedDataset(int Records, long Bytes, string Sha256);

    public static async Task<VerifiedDataset> ValidateAsync(
        string path, string method, CancellationToken cancellationToken = default)
    {
        if (method is not ("sft" or "dpo"))
            throw new InvalidDataException("Full Quick validation only supports chat SFT and paired DPO.");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidDataException("Choose an existing JSONL dataset for Quick Training.");
        if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Quick Training requires a .jsonl file.");

        // FileShare.Read avoids admitting a new writer while the file is open on
        // Windows. An expected SHA-256 is STILL checked again at job admission.
        await using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 65536, FileOptions.SequentialScan);
        if (file.Length == 0 || file.Length > MaximumBytes)
            throw new InvalidDataException("Quick Training supports nonempty JSONL files up to 256 MiB. Use Advanced for larger datasets.");
        long size = file.Length;
        string hash = Convert.ToHexString(
            await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        file.Position = 0;
        using var reader = new StreamReader(file, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);

        int count = 0, lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (line.Length > MaximumCharactersPerRecord)
                throw new InvalidDataException("Quick Training record on line " + lineNumber + " is too large.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (++count > MaximumRecords)
                throw new InvalidDataException("Quick Training supports at most 200,000 records; use Advanced for larger datasets.");
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement record = document.RootElement;
                bool valid = method == "sft"
                    ? TrainingDatasetShape.IsChatRecord(record)
                    : TrainingDatasetShape.IsPreferenceRecord(record)
                        && !string.Equals(record.GetProperty("chosen").GetString(),
                            record.GetProperty("rejected").GetString(), StringComparison.Ordinal);
                if (!valid)
                    throw new InvalidDataException("Quick " + method.ToUpperInvariant()
                        + " data on line " + lineNumber
                        + " has the wrong schema or identical chosen/rejected answers.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Invalid JSONL on line " + lineNumber + ".", ex);
            }
        }
        if (count == 0)
            throw new InvalidDataException("Quick Training dataset contains no nonempty valid records.");
        if (file.Length != size)
            throw new InvalidDataException("The training dataset changed during verification; inspect it again.");

        return new VerifiedDataset(count, size, hash);
    }
}
