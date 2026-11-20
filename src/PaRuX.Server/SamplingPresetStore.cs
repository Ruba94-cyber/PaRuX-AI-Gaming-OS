using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Named generation presets in one small atomic file, plus the rule that fills a request from one without overriding it.</summary>
public sealed class SamplingPresetStore
{
    internal const int MaximumPresets = 30;
    internal const int MaximumNameLength = 40;
    private const long MaxFileBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Lazy<SamplingPresetStore> DefaultInstance = new(() => new SamplingPresetStore(Path.Combine(ServerState.StateDirectory, "sampling-presets.json")));
    private readonly string _path;
    private readonly object _gate = new();
    private List<SamplingPreset> _items = new();

    public static SamplingPresetStore Default => DefaultInstance.Value;

    public SamplingPresetStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= MaxFileBytes)
                _items = (JsonSerializer.Deserialize<List<SamplingPreset>>(File.ReadAllText(_path), JsonOptions) ?? new())
                    .Where(p => Validate(p) is null && !string.IsNullOrWhiteSpace(p.Id)).Take(MaximumPresets).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public static string? Validate(SamplingPreset? p)
    {
        if (p is null) return "A preset is required.";
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length > MaximumNameLength || p.Name.Any(char.IsControl))
            return "A name of 1 to " + MaximumNameLength + " characters without control characters is required.";
        if (p.Temperature is { } t && (double.IsNaN(t) || t < 0 || t > 2)) return "Temperature must be between 0 and 2.";
        if (p.TopP is { } tp && (double.IsNaN(tp) || tp < 0 || tp > 1)) return "Top P must be between 0 and 1.";
        if (p.MaxTokens is <= 0 or > 131072) return "Longest reply must be between 1 and 131072 tokens.";
        if (p.Seed is < 0) return "Seed cannot be negative.";
        return null;
    }

    public IReadOnlyList<SamplingPreset> List() { lock (_gate) return _items.Select(Copy).ToList(); }

    public SamplingPreset? Get(string id) { lock (_gate) { SamplingPreset? p = _items.FirstOrDefault(x => x.Id == id); return p is null ? null : Copy(p); } }

    public (SamplingPreset? Saved, string? Error) Save(SamplingPreset input)
    {
        string? error = Validate(input);
        if (error != null) return (null, error);
        lock (_gate)
        {
            string name = input.Name.Trim();
            bool isNew = string.IsNullOrWhiteSpace(input.Id) || _items.All(x => x.Id != input.Id);
            if (isNew && _items.Count >= MaximumPresets) return (null, "At most " + MaximumPresets + " presets.");
            if (_items.Any(x => x.Id != input.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, "A preset with this name already exists.");
            SamplingPreset saved = Copy(input);
            saved.Name = name;
            saved.Id = isNew ? Guid.NewGuid().ToString("N")[..12] : input.Id;
            var next = _items.Where(x => x.Id != saved.Id).Append(saved).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Persist(next) ? (Copy(saved), null) : (null, "The preset could not be saved.");
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_items.All(x => x.Id != id)) return false;
            return Persist(_items.Where(x => x.Id != id).ToList());
        }
    }

    /// <summary>
    /// Fills the request's unset temperature, top_p, token cap and seed from the preset. Values the request already carries win.
    /// Returns false (and changes nothing) when the preset id is unknown.
    /// </summary>
    public bool TryApply(ChatCompletionRequest request, string presetId)
    {
        SamplingPreset? preset = Get(presetId);
        if (preset is null) return false;
        request.Temperature ??= preset.Temperature;
        request.TopP ??= preset.TopP;
        if (request.MaxTokens is null && request.MaxCompletionTokens is null) request.MaxTokens = preset.MaxTokens;
        request.Seed ??= preset.Seed;
        return true;
    }

    private bool Persist(List<SamplingPreset> next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _items = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static SamplingPreset Copy(SamplingPreset p) => new()
    { Id = p.Id, Name = p.Name, Temperature = p.Temperature, TopP = p.TopP, MaxTokens = p.MaxTokens, Seed = p.Seed };
}
