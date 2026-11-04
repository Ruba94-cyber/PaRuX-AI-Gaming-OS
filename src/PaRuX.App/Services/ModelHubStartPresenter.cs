using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaRuX.Services;

public enum StartReadiness { Checking, Ready, NeedsSetup, Installing, Problem }

public enum StartStepState { Unknown, Done, Todo, Running, Failed, Blocked }

public enum StartActionKind
{
    None,
    InstallEngine,
    CancelEngine,
    DownloadModel,
    UseDownloadedModel,
    OpenModels,
    OpenRuntimeSettings
}

/// <summary>One requirement of a capability: the engine/runtime that executes it, or the model it runs.</summary>
public sealed record StartStep(string Name, StartStepState State, string Detail);

public sealed record StartAction(StartActionKind Kind, string Label, bool Enabled = true);

/// <summary>One capability tile of the Model Hub "Start" board: what it does, what is still missing, one next action.</summary>
public sealed record StartCard(
    string Id,
    string Title,
    string Tagline,
    string Glyph,
    StartReadiness Readiness,
    string StatusText,
    IReadOnlyList<StartStep> Steps,
    StartAction Primary,
    StartAction? Secondary,
    double? Progress,
    bool Indeterminate)
{
    public string AutomationId => "ModelHub_Start_" + Id;
}

/// <summary>State of the engine (ComfyUI, speech, music, 3D) or text runtime a capability needs.</summary>
public sealed record StartEngineInput(
    string Title,
    bool Known,
    bool Ready,
    string State,
    long PendingBytes,
    double Progress,
    bool Indeterminate,
    string? Error);

/// <summary>State of the model a capability runs. <see cref="OfferTitle"/> is the best-fit model PaRuX would download.</summary>
public sealed record StartModelInput(
    bool Known,
    string? ActiveName,
    string? OfferTitle,
    long OfferBytes,
    string? OfferFit,
    bool OfferDownloadable,
    bool Downloading,
    double DownloadProgress,
    bool DownloadedNotInstalled);

/// <summary>Facts for one capability. A null engine/model means the capability has no such requirement.</summary>
public sealed record StartCapabilityInput(string Id, StartEngineInput? Engine, StartModelInput? Model);

public sealed record StartSummary(int Ready, int Total, string Line);

/// <summary>
/// Pure presenter of the Model Hub "Start" board (Locally-Uncensored-style "what can I do and what is missing"). It turns
/// engine and model facts into plain-language cards with exactly one next action, so a person does not have to know that
/// images need both an engine (ComfyUI) and a model, or which tab each of them lives in. It never starts anything itself.
/// </summary>
public static class ModelHubStartPresenter
{
    public static readonly IReadOnlyList<string> CapabilityOrder =
        new[] { "chat", "image", "edit", "video", "speech", "music", "3d" };

    /// <summary>Engine-based capabilities whose engine can be installed from the Hub; the text runtime is managed in Settings.</summary>
    public static bool EngineInstallsInHub(string id) => !string.Equals(id, "chat", StringComparison.Ordinal);

    public static IReadOnlyList<StartCard> Build(IEnumerable<StartCapabilityInput> inputs, bool italian) =>
        inputs.Select(i => BuildCard(i, italian)).ToList();

    public static StartSummary Summarize(IReadOnlyList<StartCard> cards, bool italian)
    {
        int ready = cards.Count(c => c.Readiness == StartReadiness.Ready);
        int total = cards.Count;
        string line = italian ? $"{ready} su {total} funzioni pronte" : $"{ready} of {total} abilities ready";
        return new StartSummary(ready, total, line);
    }

    public static string FitWords(string? fitClass, bool italian) => (fitClass ?? string.Empty).ToLowerInvariant() switch
    {
        "fits" => italian ? "gira bene sulla tua GPU" : "runs well on your GPU",
        "offload" => italian ? "funziona, ma più lento" : "works, but slower",
        "too_large" => italian ? "troppo grande per questo PC" : "too large for this PC",
        _ => string.Empty
    };

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        const double gb = 1024.0 * 1024 * 1024;
        return bytes >= gb
            ? (bytes / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    private static StartCard BuildCard(StartCapabilityInput c, bool it)
    {
        (string title, string tagline, string glyph) = Meta(c.Id, it);
        StartEngineInput? e = c.Engine;
        StartModelInput? m = c.Model;

        StartStepState engineState = EngineState(e);
        StartStepState modelState = ModelState(m);

        var steps = new List<StartStep>();
        if (e != null) steps.Add(new StartStep(e.Title, engineState, EngineDetail(c.Id, e, engineState, it)));
        if (m != null) steps.Add(new StartStep(it ? "Modello" : "Model", modelState, ModelDetail(m, modelState, it)));

        StartStepState[] states = steps.Select(s => s.State).ToArray();
        StartReadiness readiness =
            states.Length == 0 || states.Any(s => s == StartStepState.Unknown) ? StartReadiness.Checking
            : states.Any(s => s is StartStepState.Blocked or StartStepState.Failed) ? StartReadiness.Problem
            : states.Any(s => s == StartStepState.Running) ? StartReadiness.Installing
            : states.All(s => s == StartStepState.Done) ? StartReadiness.Ready
            : StartReadiness.NeedsSetup;

        double? progress = null;
        bool indeterminate = false;
        if (engineState == StartStepState.Running && e != null) { progress = e.Progress; indeterminate = e.Indeterminate; }
        else if (modelState == StartStepState.Running && m != null) progress = m.DownloadProgress;

        string status = readiness switch
        {
            StartReadiness.Ready => it ? "Pronto" : "Ready",
            StartReadiness.Checking => it ? "Controllo…" : "Checking…",
            StartReadiness.Installing => (modelState == StartStepState.Running && engineState != StartStepState.Running
                    ? (it ? "Download" : "Downloading")
                    : (it ? "Installazione" : "Installing"))
                + (progress.HasValue && !indeterminate ? " " + Percent(progress.Value) : "…"),
            StartReadiness.Problem => engineState == StartStepState.Blocked
                ? (it ? "Non disponibile su questo PC" : "Not available on this PC")
                : (it ? "Serve attenzione" : "Needs attention"),
            _ => StepsLeft(steps.Count(s => s.State is StartStepState.Todo), it)
        };

        (StartAction primary, StartAction? secondary) = Actions(c, e, m, engineState, modelState, it);
        return new StartCard(c.Id, title, tagline, glyph, readiness, status, steps, primary, secondary, progress, indeterminate);
    }

    private static StartStepState EngineState(StartEngineInput? e)
    {
        if (e == null) return StartStepState.Done;
        if (!e.Known) return StartStepState.Unknown;
        if (e.Ready) return StartStepState.Done;
        return (e.State ?? string.Empty).ToLowerInvariant() switch
        {
            "running" => StartStepState.Running,
            "failed" => StartStepState.Failed,
            "blocked" => StartStepState.Blocked,
            _ => StartStepState.Todo
        };
    }

    private static StartStepState ModelState(StartModelInput? m)
    {
        if (m == null) return StartStepState.Done;
        if (!m.Known) return StartStepState.Unknown;
        if (!string.IsNullOrWhiteSpace(m.ActiveName)) return StartStepState.Done;
        return m.Downloading ? StartStepState.Running : StartStepState.Todo;
    }

    private static string EngineDetail(string id, StartEngineInput e, StartStepState state, bool it)
    {
        string size = FormatSize(e.PendingBytes);
        return state switch
        {
            StartStepState.Done => it ? "Installato" : "Installed",
            StartStepState.Running => (it ? "Installazione in corso" : "Installing now")
                + (e.Indeterminate ? "…" : " · " + Percent(e.Progress)),
            StartStepState.Failed => string.IsNullOrWhiteSpace(e.Error)
                ? (it ? "Installazione interrotta" : "Setup stopped")
                : e.Error!,
            StartStepState.Blocked => it ? "Non disponibile su questo PC" : "Not available on this PC",
            StartStepState.Unknown => it ? "Controllo in corso…" : "Checking…",
            _ => id == "chat"
                ? (it ? "Non ancora configurato" : "Not set up yet")
                : (it ? "Non installato" : "Not installed") + (size.Length > 0 ? " · " + size : string.Empty)
        };
    }

    private static string ModelDetail(StartModelInput m, StartStepState state, bool it)
    {
        switch (state)
        {
            case StartStepState.Done:
                return m.ActiveName!;
            case StartStepState.Running:
                return (it ? "Download in corso" : "Downloading") + " · " + Percent(m.DownloadProgress);
            case StartStepState.Unknown:
                return it ? "Controllo in corso…" : "Checking…";
        }
        if (m.DownloadedNotInstalled) return it ? "Scaricato, manca solo l'attivazione" : "Downloaded, just needs activating";
        if (string.IsNullOrWhiteSpace(m.OfferTitle))
            return it ? "Nessun modello adatto trovato per ora" : "No suitable model found yet";
        var parts = new List<string> { m.OfferTitle! };
        string size = FormatSize(m.OfferBytes);
        if (size.Length > 0) parts.Add(size);
        if (!string.IsNullOrWhiteSpace(m.OfferFit)) parts.Add(m.OfferFit!);
        return (it ? "Consigliato: " : "Suggested: ") + string.Join(" · ", parts);
    }

    private static (StartAction Primary, StartAction? Secondary) Actions(
        StartCapabilityInput c, StartEngineInput? e, StartModelInput? m,
        StartStepState engineState, StartStepState modelState, bool it)
    {
        switch (engineState)
        {
            case StartStepState.Unknown:
                return (new StartAction(StartActionKind.None, it ? "Controllo…" : "Checking…", false), null);
            case StartStepState.Running:
                return (new StartAction(StartActionKind.CancelEngine, it ? "Annulla installazione" : "Cancel installation"), null);
            case StartStepState.Blocked:
                return (new StartAction(StartActionKind.None, it ? "Non disponibile" : "Not available", false), null);
            case StartStepState.Todo:
            case StartStepState.Failed:
                {
                    bool retry = engineState == StartStepState.Failed;
                    if (!EngineInstallsInHub(c.Id))
                        return (new StartAction(StartActionKind.OpenRuntimeSettings,
                            it ? "Configura il motore di chat" : "Set up the chat engine"), null);
                    string size = e != null ? FormatSize(e.PendingBytes) : string.Empty;
                    string label = retry
                        ? (it ? "Riprova" : "Try again")
                        : (it ? "Installa il motore" : "Install the engine") + (size.Length > 0 ? " (" + size + ")" : string.Empty);
                    return (new StartAction(StartActionKind.InstallEngine, label), null);
                }
        }

        switch (modelState)
        {
            case StartStepState.Unknown:
                return (new StartAction(StartActionKind.None, it ? "Controllo…" : "Checking…", false), null);
            case StartStepState.Running:
                return (new StartAction(StartActionKind.None, it ? "Download in corso…" : "Downloading…", false), null);
            case StartStepState.Todo:
                if (m!.DownloadedNotInstalled)
                    return (new StartAction(StartActionKind.UseDownloadedModel, it ? "Completa e usa" : "Finish and use"), null);
                if (!string.IsNullOrWhiteSpace(m.OfferTitle) && m.OfferDownloadable)
                {
                    string size = FormatSize(m.OfferBytes);
                    return (new StartAction(StartActionKind.DownloadModel,
                        (it ? "Scarica il modello" : "Download the model") + (size.Length > 0 ? " (" + size + ")" : string.Empty)), null);
                }
                return (new StartAction(StartActionKind.OpenModels, it ? "Scegli un modello" : "Choose a model"), null);
        }

        return (new StartAction(StartActionKind.None, it ? "Pronto" : "Ready", false),
            m != null ? new StartAction(StartActionKind.OpenModels, it ? "Cambia modello" : "Change model") : null);
    }

    private static string StepsLeft(int n, bool it) => n <= 1
        ? (it ? "Manca 1 passaggio" : "1 step left")
        : (it ? $"Mancano {n} passaggi" : $"{n} steps left");

    private static string Percent(double fraction) =>
        Math.Clamp((int)Math.Round(fraction * 100), 0, 100).ToString(CultureInfo.InvariantCulture) + "%";

    private static (string Title, string Tagline, string Glyph) Meta(string id, bool it) => id switch
    {
        "chat" => (it ? "Chat e scrittura" : "Chat & writing",
            it ? "Parla, scrivi, riassumi e programma con un modello locale." : "Talk, write, summarise and code with a local model.", ""),
        "image" => (it ? "Immagini" : "Images",
            it ? "Crea immagini partendo da una descrizione." : "Create images from a description.", ""),
        "edit" => (it ? "Modifica immagini" : "Image editing",
            it ? "Cambia o correggi un'immagine esistente." : "Change or fix an existing picture.", ""),
        "video" => (it ? "Video" : "Video",
            it ? "Trasforma un testo o un'immagine in un breve video." : "Turn a prompt or a picture into a short clip.", ""),
        "speech" => (it ? "Voce" : "Voice",
            it ? "Voce naturale per leggere i testi ad alta voce." : "A natural voice that reads text aloud.", ""),
        "music" => (it ? "Musica" : "Music",
            it ? "Componi musica da una descrizione." : "Compose music from a description.", ""),
        "3d" => ("3D",
            it ? "Trasforma un'immagine in un modello 3D." : "Turn a picture into a 3D model.", ""),
        _ => (id, string.Empty, "")
    };
}
