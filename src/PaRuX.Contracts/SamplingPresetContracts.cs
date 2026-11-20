namespace PaRuX.Contracts;

/// <summary>
/// A named set of generation settings the owner can pick for a chat ("Precise", "Creative"), like LM Studio's presets.
/// Every value is optional; an unset value means "let PaRuX decide". A value the request itself carries always wins.
/// </summary>
public sealed class SamplingPreset
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    public long? Seed { get; set; }
}
