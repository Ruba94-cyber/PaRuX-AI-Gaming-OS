using System;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Side-by-side Chat comparison data, backed by the EXISTING local Arena result.</summary>
public sealed record QuickChatComparisonColumn(
    string ModelFileName, string StatusText, string AnswerText, bool AnswerAvailable);

public sealed record QuickChatComparisonPresentation(
    bool Ready, string Prompt, string StatusText,
    QuickChatComparisonColumn Left, QuickChatComparisonColumn Right,
    string? UserPreferredModel);

public static class QuickChatComparisonPresenter
{
    /// <summary>Shared wire projection; no inferred timing, quality ranking or missing answer.</summary>
    public static ArenaQuickCompareViewDto ToDto(ArenaRunDto? run, bool italian)
    {
        QuickChatComparisonPresentation p = Build(run, italian);
        return new ArenaQuickCompareViewDto(p.Ready, p.Prompt, p.StatusText,
            new ArenaQuickCompareColumnDto(p.Left.ModelFileName, p.Left.StatusText,
                p.Left.AnswerText, p.Left.AnswerAvailable),
            new ArenaQuickCompareColumnDto(p.Right.ModelFileName, p.Right.StatusText,
                p.Right.AnswerText, p.Right.AnswerAvailable),
            p.UserPreferredModel);
    }

    public static QuickChatComparisonPresentation Build(ArenaRunDto? run, bool italian)
    {
        static QuickChatComparisonColumn Empty() => new(string.Empty, string.Empty, string.Empty, false);
        if (run is null || run.ModelFileNames is not { Count: 2 }
            || run.Prompts is not { Count: 1 }
            || !string.Equals(run.Prompts[0].TaskId, "custom", StringComparison.Ordinal))
            return new(false, string.Empty,
                italian ? "Confronto non disponibile." : "Comparison not available.",
                Empty(), Empty(), null);

        ArenaPromptResultDto prompt = run.Prompts[0];
        QuickChatComparisonColumn Column(string model)
        {
            ArenaModelResultDto? result = prompt.Results?.FirstOrDefault(r =>
                string.Equals(r.ModelFileName, model, StringComparison.OrdinalIgnoreCase));
            if (result is null)
                return new(model, italian ? "In attesa" : "Pending", string.Empty, false);
            return result.Status switch
            {
                "completed" when !string.IsNullOrWhiteSpace(result.Output) =>
                    new(model, italian ? "Risposta ricevuta" : "Answered", result.Output, true),
                "completed" => new(model, italian ? "Risposta vuota" : "Empty response", string.Empty, false),
                "failed" => new(model, italian ? "Errore" : "Failed", string.Empty, false),
                "timed_out" => new(model, italian ? "Tempo scaduto" : "Timed out", string.Empty, false),
                "cancelled" => new(model, italian ? "Annullato" : "Cancelled", string.Empty, false),
                "skipped" => new(model, italian ? "Non eseguito" : "Skipped", string.Empty, false),
                _ => new(model, italian ? "In attesa" : "Pending", string.Empty, false)
            };
        }

        QuickChatComparisonColumn left = Column(run.ModelFileNames[0]);
        QuickChatComparisonColumn right = Column(run.ModelFileNames[1]);
        string status = run.Status switch
        {
            "running" => italian ? "Confronto locale in corso" : "Local comparison running",
            "completed" => italian ? "Confronto terminato" : "Comparison finished",
            "cancelled" => italian ? "Confronto annullato" : "Comparison cancelled",
            _ => italian ? "Confronto non riuscito" : "Comparison failed"
        };
        // A pick is only a user judgement of a completed answer, not an automatic winner.
        string? pick = string.Equals(prompt.UserPick, left.ModelFileName, StringComparison.OrdinalIgnoreCase)
            && left.AnswerAvailable ? left.ModelFileName
            : string.Equals(prompt.UserPick, right.ModelFileName, StringComparison.OrdinalIgnoreCase)
                && right.AnswerAvailable ? right.ModelFileName : null;

        return new(true, prompt.Prompt, status, left, right, pick);
    }
}
