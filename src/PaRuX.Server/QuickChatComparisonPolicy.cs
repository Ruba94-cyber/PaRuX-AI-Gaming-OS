using System;
using System.IO;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Converts a Chat "compare two installed models" intention into the EXISTING local
/// Model Arena Core request. This policy does not call a provider or change Chat routing.
/// ModelArenaService remains the authority for installed-model checks and Gaming hard-off.
/// </summary>
public static class QuickChatComparisonPolicy
{
    public static ArenaStartRequest Create(string? firstModel, string? secondModel,
        string? samePrompt, int maxTokens = 256)
    {
        string first = ValidateModelName(firstModel);
        string second = ValidateModelName(secondModel);
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose two different installed models.");
        if (string.IsNullOrWhiteSpace(samePrompt) || samePrompt.Trim().Length > ModelArenaService.MaxPromptChars)
            throw new InvalidDataException("Write one prompt of up to " + ModelArenaService.MaxPromptChars + " characters.");
        if (maxTokens < 64 || maxTokens > 1024)
            throw new InvalidDataException("The token limit must be between 64 and 1024.");
        return new ArenaStartRequest
        {
            ModelFileNames = new() { first, second },
            TaskIds = new(),
            CustomPrompt = samePrompt.Trim(),
            MaxTokens = maxTokens
        };
    }

    public static string AutoExplanation(bool italian) => italian
        ? "Auto sceglie tra i modelli locali installati in base alla richiesta e alle risorse disponibili. "
          + "Il confronto non cambia il modello usato dalla Chat."
        : "Auto chooses among installed local models for your request and the available resources. "
          + "A comparison does not change the model used by Chat.";

    private static string ValidateModelName(string? value)
    {
        string name = value?.Trim() ?? string.Empty;
        // A model identifier in Arena is always a basename; never accept arbitrary paths.
        if (name.Length == 0 || name.Length > 240 || name == "." || name == ".."
            || name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0
            || string.Equals(name, "Auto", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an installed model by file name, not a path or Auto.");
        return name;
    }
}

/// <summary>Conservative UI suggestions only; actual resource admission stays with Core.</summary>
public sealed record MediaHardwarePreset(
    string Kind, int Width, int Height, int Frames, bool RequiresConfirmation,
    bool Suggested, string Explanation);

public static class MediaHardwarePresetPolicy
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    /// Uses only supplied, locally measured free VRAM. Unknown is zero, not assumed.
    /// Presets are estimates and never authorize starting a model or stealing game memory.
    /// </summary>
    public static MediaHardwarePreset Suggest(string kind, long freeVramBytes, bool gameActive)
    {
        string mode = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (mode != "image" && mode != "video")
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (gameActive)
            return new(mode, 0, 0, 0, true, false,
                "A game is active. AI media jobs remain blocked unless the user explicitly releases game resources.");
        if (freeVramBytes <= 0)
            return new(mode, 0, 0, 0, true, false,
                "Free GPU memory is unknown. Pick a local runtime and confirm resources before generating.");
        if (mode == "image")
            return freeVramBytes >= 12 * GiB
                ? new(mode, 1024, 1024, 1, false, true, "Suggested starting size; actual model fit is checked by Core.")
                : new(mode, 512, 512, 1, true, true, "Conservative starting size. Confirm the model fits before running.");
        return freeVramBytes >= 16 * GiB
            ? new(mode, 768, 432, 49, true, true, "Short video starting point, not a performance guarantee.")
            : new(mode, 512, 288, 25, true, true, "Short preview only; confirm actual video-engine memory needs.");
    }
}
