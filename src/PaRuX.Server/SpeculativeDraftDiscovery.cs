using System.Text.RegularExpressions;

namespace PaRuX.Server;

/// <summary>
/// Finds explicitly compatible local speculative sidecars without ever downloading or guessing a
/// remote model. Discovery is deliberately conservative: a draft must live in a PaRuX-managed
/// model directory and its normalized filename must contain the normalized target family stem.
/// This prevents an unrelated EAGLE/DFlash checkpoint from being attached to a target merely
/// because both happen to share a tokenizer family.
/// </summary>
public static partial class SpeculativeDraftDiscovery
{
    public sealed record Candidate(string Path, string Type, int DraftMax, string Reason);

    public static IReadOnlyList<Candidate> Discover(string targetModelPath)
    {
        if (string.IsNullOrWhiteSpace(targetModelPath) || !File.Exists(targetModelPath)) return Array.Empty<Candidate>();
        string targetStem = NormalizeTargetStem(Path.GetFileNameWithoutExtension(targetModelPath));
        if (targetStem.Length < 4) return Array.Empty<Candidate>();

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetModelPath));
        if (!string.IsNullOrWhiteSpace(targetDirectory)) roots.Add(targetDirectory);
        roots.Add(Path.GetFullPath(LocalAiPaths.ModelsPath));
        roots.Add(Path.GetFullPath(ModelDownloadService.ModelsDirectory));

        var found = new List<Candidate>();
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.gguf", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (string file in files)
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(targetModelPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                string name = Path.GetFileNameWithoutExtension(file);
                string lower = name.ToLowerInvariant();
                string type = DetectType(lower);
                if (type == "none") continue;
                string candidateStem = NormalizeTargetStem(RemoveSpeculatorMarkers(name));
                bool explicitSidecar = candidateStem.Contains(targetStem, StringComparison.OrdinalIgnoreCase)
                    || targetStem.Contains(candidateStem, StringComparison.OrdinalIgnoreCase);
                if (!explicitSidecar) continue;

                int draftMax = type switch
                {
                    "draft-dspark" => 7,
                    "draft-dflash" => 15,
                    "draft-eagle3" => 8,
                    "draft-mtp" => 8,
                    _ => 6
                };
                found.Add(new Candidate(Path.GetFullPath(file), type, draftMax,
                    "Local managed sidecar matched target family '" + targetStem + "'."));
            }
        }

        var result = found
            .OrderBy(candidate => Rank(candidate.Type))
            .ThenBy(candidate => new FileInfo(candidate.Path).Length)
            .Take(3)
            .ToList();

        // A plain smaller model of the same family (for example a 0.6B beside an 8B) is the classic draft model.
        // It is only a CANDIDATE: llama-server refuses an incompatible vocabulary and the calibration race keeps it only
        // when decode is at least 5% faster with a byte-identical greedy output (RuntimeOptimizationService gates).
        var sized = new List<(string Path, long Size)>();
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.gguf", SearchOption.TopDirectoryOnly))
                    sized.Add((Path.GetFullPath(file), new FileInfo(file).Length));
            }
            catch { }
        }
        long targetSize = new FileInfo(targetModelPath).Length;
        foreach (Candidate small in SelectSameFamilySmallModels(Path.GetFullPath(targetModelPath), targetSize, sized))
            if (!result.Any(existing => string.Equals(existing.Path, small.Path, StringComparison.OrdinalIgnoreCase)))
                result.Add(small);
        return result;
    }

    /// <summary>Largest share of the target's file size a plain draft model may take. A draft that is not much smaller than
    /// the target cannot pay for its own forward passes.</summary>
    internal const double MaxDraftSizeFraction = 0.25;
    /// <summary>Below this a GGUF is a projector/stub, not a language model.</summary>
    internal const long MinDraftBytes = 50L * 1024 * 1024;
    /// <summary>Smallest target file for which a plain draft model is even raced. Not validated above this size (no larger same-family pair was measured).</summary>
    internal const long MinTargetBytesForPlainDraft = 6L * 1024 * 1024 * 1024;

    /// <summary>
    /// Pure selection of same-family smaller models by file name and size only (no I/O). Family = the model name with
    /// parameter counts, quantisation, instruct/chat markers and split tokens removed ("Qwen3-8B-Instruct-Q4_K_M" and
    /// "Qwen3-0.6B-Q8_0" both become "qwen3"). A variant of the family with a different suffix ("Qwen3-Coder") is NOT the
    /// same family. At most two are returned: the two largest that stay within <see cref="MaxDraftSizeFraction"/>.
    /// </summary>
    internal static IReadOnlyList<Candidate> SelectSameFamilySmallModels(
        string targetPath, long targetSizeBytes, IEnumerable<(string Path, long Size)> files)
    {
        // Measured on an RTX 5080 with Qwen3.5-4B (0.8B same-family draft, 4.2 GiB Q8_0 .. 2.2 GiB Q3_K_M targets): speculation was
        // 0.22x-0.54x of plain decode with 34-89% acceptance and non-identical greedy output. Small targets already decode at
        // 150-200 tok/s, so the draft cannot pay for itself; do not spend calibration time on them.
        if (targetSizeBytes < MinTargetBytesForPlainDraft) return Array.Empty<Candidate>();
        string targetFile = System.IO.Path.GetFileNameWithoutExtension(targetPath);
        string family = FamilyKey(targetFile);
        if (family.Length < 4 || PaRuX.Intelligence.GgufFileNaming.IsProjector(targetFile)) return Array.Empty<Candidate>();
        double? targetParams = ParameterBillions(targetFile);

        var picks = new List<(string Path, long Size)>();
        foreach ((string path, long size) in files)
        {
            if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase)) continue;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (size < MinDraftBytes || size > targetSizeBytes * MaxDraftSizeFraction) continue;
            if (PaRuX.Intelligence.GgufFileNaming.IsProjector(name) || DetectType(name.ToLowerInvariant()) != "none") continue;
            if (PaRuX.Intelligence.GgufFileNaming.TryParseSplitPart(name, out _)) continue;
            if (!string.Equals(FamilyKey(name), family, StringComparison.Ordinal)) continue;
            double? p = ParameterBillions(name);
            // When both sizes are named, the draft must really be smaller by at least 3x.
            if (p is not null && targetParams is not null && p.Value * 3 > targetParams.Value) continue;
            picks.Add((path, size));
        }
        return picks
            .OrderByDescending(f => f.Size).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(f => new Candidate(f.Path, "draft-simple", 8,
                "Same-family smaller model '" + family + "' (" + (f.Size / (1024 * 1024)) + " MiB) as a draft candidate; kept only if the local race measures >=5% faster decode with identical greedy output."))
            .ToList();
    }

    internal static string FamilyKey(string fileNameWithoutExtension)
    {
        string v = fileNameWithoutExtension.ToLowerInvariant();
        v = SplitPartRegex().Replace(v, "");
        v = QuantSuffixRegex().Replace(v, "");
        v = ParamTokenRegex().Replace(v, "-");
        v = FamilyNoiseRegex().Replace(v, "-");
        v = NoiseRegex().Replace(v, "-");
        return v.Trim('-');
    }

    internal static double? ParameterBillions(string name)
    {
        // The total size token ("30b" in "30b-a3b"); active-parameter tokens ("a3b") are skipped by the lookbehind.
        Match m = ParamValueRegex().Match(name.ToLowerInvariant());
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double b) ? b : null;
    }

    internal static string NormalizeTargetStem(string value)
    {
        value = value.ToLowerInvariant();
        value = QuantSuffixRegex().Replace(value, "");
        value = NoiseRegex().Replace(value, "-");
        return value.Trim('-','_','.');
    }

    private static string RemoveSpeculatorMarkers(string value) => SpeculatorMarkerRegex().Replace(value, "");

    private static string DetectType(string lower)
    {
        if (lower.Contains("eagle3")) return "draft-eagle3";
        if (lower.Contains("dflash")) return "draft-dflash";
        if (lower.Contains("dspark")) return "draft-dspark";
        if (lower.Contains("mtp")) return "draft-mtp";
        if (lower.Contains("draft") || lower.Contains("speculator")) return "draft-simple";
        return "none";
    }

    private static int Rank(string type) => type switch
    {
        "draft-eagle3" => 0,
        "draft-dspark" => 1,
        "draft-dflash" => 2,
        "draft-mtp" => 3,
        _ => 4
    };

    [GeneratedRegex(@"(?i)(?:[-_.](?:q\d(?:_\d)?|iq\d[^-_.]*|f16|bf16|fp16|fp32|k_[msl]|gguf))*$")]
    private static partial Regex QuantSuffixRegex();

    [GeneratedRegex(@"(?i)(?:eagle3|dflash|dspark|mtp|speculator|draft(?:-simple)?)")]
    private static partial Regex SpeculatorMarkerRegex();

    [GeneratedRegex(@"(?<![a-z0-9])a?\d+(?:\.\d+)?[bm](?![a-z0-9])")]
    private static partial Regex ParamTokenRegex();

    [GeneratedRegex(@"(?<![a-z0-9.])(\d+(?:\.\d+)?)b(?![a-z0-9])")]
    private static partial Regex ParamValueRegex();

    [GeneratedRegex(@"(?<![a-z0-9])(?:instruct|chat|it|base|gguf|ud|unsloth|imatrix|i1|bf16|fp16|f16|thinking|2507|i?q\d(?:_[a-z0-9]+)*)(?![a-z0-9])")]
    private static partial Regex FamilyNoiseRegex();

    [GeneratedRegex(@"-\d{5}-of-\d{5}")]
    private static partial Regex SplitPartRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NoiseRegex();
}
