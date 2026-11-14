using System.Diagnostics;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local speech: Parakeet-TDT-0.6B-v3 for STT and Kokoro-82M for TTS through a PaRuX-managed
/// Python environment outside the repository. The venv and its interpreter are created through
/// ManagedPythonRuntime, so a user-installed Python/uv is never a prerequisite. Long-lived JSONL
/// workers keep the heavy STT/TTS models warm between turns; a worker failure falls back to the
/// one-shot path and the following request attempts a clean worker restart. Gaming Mode remains a
/// hard-off: resident speech workers are killed and neither warm nor one-shot inference may start.
/// </summary>
public sealed class SpeechService : IDisposable, IContinuousSpeechStatusProvider
{
    private static string RuntimeRoot => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "speech");

    private static string VenvRoot => Path.Combine(RuntimeRoot, ".venv");
    private static string VenvPython => ManagedPythonRuntime.VenvPython(VenvRoot);
    private static string ScriptPath => Path.Combine(RuntimeRoot, "parux_speech.py");

    private readonly object _workerGate = new();
    private readonly LocalRuntimeService? _runtime;
    private readonly SpeechProcessAdmission _admission;
    private SpeechWarmWorker? _sttWorker;
    private SpeechWarmWorker? _ttsWorker;
    private bool _disposed;

    private readonly Func<bool> _gamingActive;
    private readonly Func<CancellationToken> _gamingToken;
    private readonly Func<string, SpeechWarmWorker>? _workerFactory;
    private readonly Func<bool> _engineInstalled;

    public SpeechService()
    {
        _admission = new SpeechProcessAdmission(() => false);
        _gamingActive = () => false;
        _gamingToken = () => CancellationToken.None;
        _engineInstalled = () => ManagedPythonRuntime.IsManagedVenv(VenvRoot);
    }

    public SpeechService(LocalRuntimeService runtime)
    {
        _runtime = runtime;
        _admission = SpeechProcessAdmission.For(runtime);
        _gamingActive = () => runtime.GamingMode;
        _gamingToken = () => runtime.GamingCancellationToken;
        _engineInstalled = () => ManagedPythonRuntime.IsManagedVenv(VenvRoot);
    }

    /// <summary>Test seam: a service whose Gaming signal, installed check and warm workers are injected, so the
    /// hard-off paths run against fake child processes without a managed Python environment or script on disk.</summary>
    internal SpeechService(
        SpeechProcessAdmission admission,
        Func<bool> gamingActive,
        Func<CancellationToken> gamingToken,
        Func<string, SpeechWarmWorker> workerFactory)
    {
        _admission = admission;
        _gamingActive = gamingActive;
        _gamingToken = gamingToken;
        _workerFactory = workerFactory;
        _engineInstalled = () => true;
    }

    public async Task<SpeechEngineStatus> GetStatusAsync(CancellationToken ct)
    {
        if (_gamingActive())
            return new SpeechEngineStatus { SttInstalled = false, TtsInstalled = false };
        if (!ManagedPythonRuntime.IsManagedVenv(VenvRoot)) return new SpeechEngineStatus { SttInstalled = false, TtsInstalled = false };
        try
        {
            (int exitCode, string stdout, _) = await RunCapturedAsync(VenvPython,
                "-c \"import importlib.util as u; print(u.find_spec('parakeet_tdt') is not None or u.find_spec('nemo') is not None or u.find_spec('transformers') is not None); print(u.find_spec('kokoro') is not None)\"",
                ct, TimeSpan.FromSeconds(20), admission: _admission, generation: _admission.Capture());
            string[] lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new SpeechEngineStatus
            {
                SttInstalled = exitCode == 0 && lines.Length > 0 && lines[0] == "True",
                TtsInstalled = exitCode == 0 && lines.Length > 1 && lines[1] == "True"
            };
        }
        catch
        {
            return new SpeechEngineStatus { SttInstalled = false, TtsInstalled = false };
        }
    }

    /// <summary>
    /// Kokoro's English G2P (misaki[en]) needs the spaCy en_core_web_sm model but tries to download it
    /// on the first request. A PaRuX-managed venv has no pip and uv is not on PATH there, so that lazy
    /// download fails and the streaming worker exits on the first English turn (found in the BIC-13 live
    /// run). The MIT-licensed model wheel is installed up front, pinned to the spaCy 3.8 series.
    /// </summary>
    internal const string SpacyModelRequirement =
        "en_core_web_sm @ https://github.com/explosion/spacy-models/releases/download/en_core_web_sm-3.8.0/en_core_web_sm-3.8.0-py3-none-any.whl";

    internal static readonly string[] SpeechPackages =
    {
        "transformers", "soundfile", "numpy", "librosa", "kokoro", "misaki[en]", SpacyModelRequirement
    };

    public async Task<string> InstallEngineAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeRoot);
        var log = new System.Text.StringBuilder();
        TorchHardwareProfile torchHardware = await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false);
        TorchInstallChannelPlan torchPlan = TorchInstallChannelPolicy.Select(torchHardware);
        if (!torchPlan.CanInstall) throw new InvalidOperationException(torchPlan.Reason);

        await ManagedPythonRuntime.EnsureVenvAsync(VenvRoot, ManagedPythonRuntime.DefaultPythonVersion, ct)
            .ConfigureAwait(false);

        log.AppendLine(torchPlan.Reason);
        log.Append(await ManagedPythonRuntime.PipInstallAsync(
            VenvRoot,
            torchPlan.PipArguments,
            ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));
        log.Append(await ManagedPythonRuntime.PipInstallAsync(
            VenvRoot,
            ManagedRequirementLocks.ArgumentsFor("speech"),
            ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));
        PrepareScript();
        return log.ToString();
    }

    /// <summary>Loads one or both local models before the first live voice turn.</summary>
    public Task PrewarmAsync(bool stt, bool tts, CancellationToken ct) =>
        RunGamingAwareVoidAsync(ct, linked => PrewarmCoreAsync(stt, tts, linked));

    public Task<TranscribeResponse> TranscribeAsync(TranscribeRequest request, CancellationToken ct) =>
        RunGamingAwareAsync(ct, linked => TranscribeCoreAsync(request, linked));

    public Task<SynthesizeResponse> SynthesizeAsync(SynthesizeRequest request, CancellationToken ct) =>
        RunGamingAwareAsync(ct, linked => SynthesizeCoreAsync(request, linked));

    /// <summary>
    /// Every speech request runs under a token that the Gaming transition cancels, so an in-flight STT/TTS request
    /// is interrupted at once. Whatever the interruption looks like further down (a cancelled token, a killed child
    /// that closed its pipe, a one-shot process that exited non-zero, a stale admission ticket), the caller gets the
    /// one typed "unavailable during Gaming" error rather than a misleading generic failure. A cancel by the caller
    /// itself is never rewritten.
    /// </summary>
    private async Task<T> RunGamingAwareAsync<T>(CancellationToken ct, Func<CancellationToken, Task<T>> body)
    {
        // Only the Gaming gate here: request validation and the installed check stay in the core, in their old order.
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gamingActive() || _admission.IsClosed)
            throw new SpeechUnavailableDuringGamingException("Gaming Mode is active, so local speech inference is unavailable.");
        long ticket;
        try { ticket = _admission.Capture(); }
        catch (InvalidOperationException ex) { throw GamingInterrupted(ex); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _gamingToken());
        try
        {
            return await body(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SpeechUnavailableDuringGamingException
            && IsGamingTransition(ex, ct, ticket))
        {
            // Make sure no child outlives the rewritten failure.
            if (!_admission.IsClosed) StopResidentWorkers();
            throw GamingInterrupted(ex);
        }
    }

    private async Task RunGamingAwareVoidAsync(CancellationToken ct, Func<CancellationToken, Task> body) =>
        await RunGamingAwareAsync<bool>(ct, async linked => { await body(linked).ConfigureAwait(false); return true; })
            .ConfigureAwait(false);

    private void StopResidentWorkers()
    {
        lock (_workerGate)
        {
            _sttWorker?.Stop();
            _ttsWorker?.Stop();
        }
    }

    private async Task PrewarmCoreAsync(bool stt, bool tts, CancellationToken ct)
    {
        EnsureInferenceAllowed();
        long generation = _admission.Capture();
        PrepareScript();
        var tasks = new List<Task>(2);
        if (stt) tasks.Add(GetWorker("transcribe").PrewarmAsync(ct, TimeSpan.FromMinutes(3), generation));
        if (tts) tasks.Add(GetWorker("synthesize").PrewarmAsync(ct, TimeSpan.FromMinutes(3), generation));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<TranscribeResponse> TranscribeCoreAsync(TranscribeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AudioBase64)) throw new InvalidDataException("audioBase64 is required.");
        EnsureInferenceAllowed();
        long generation = _admission.Capture();
        PrepareScript();

        byte[] audioBytes;
        try { audioBytes = Convert.FromBase64String(request.AudioBase64); }
        catch (FormatException) { throw new InvalidDataException("audioBase64 is not valid Base64 audio data."); }
        if (audioBytes.Length == 0) throw new InvalidDataException("audioBase64 decoded to an empty audio payload.");

        string extension;
        try { extension = Path.GetExtension(request.FileName); }
        catch (ArgumentException) { extension = string.Empty; }
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12) extension = ".wav";

        string audioPath = Path.Combine(Path.GetTempPath(), "parux-stt-" + Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(audioPath, audioBytes, ct);
        try
        {
            JsonElement result;
            try
            {
                result = await GetWorker("transcribe").ExecuteAsync(new
                {
                    audioPath,
                    language = request.Language ?? string.Empty
                }, ct, TimeSpan.FromMinutes(3), generation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                EnsureInferenceAllowed();
                _admission.Check(generation);
                result = await RunTranscribeOneShotAsync(audioPath, request.Language ?? string.Empty, ct, generation).ConfigureAwait(false);
            }

            return new TranscribeResponse
            {
                Text = result.GetProperty("text").GetString() ?? string.Empty,
                LanguageDetected = result.TryGetProperty("language", out JsonElement lang) ? lang.GetString() ?? string.Empty : string.Empty,
                DurationSeconds = result.TryGetProperty("duration", out JsonElement dur) ? dur.GetDouble() : 0
            };
        }
        finally
        {
            try { File.Delete(audioPath); } catch { }
        }
    }

    private async Task<SynthesizeResponse> SynthesizeCoreAsync(SynthesizeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new InvalidDataException("text is required.");
        EnsureInferenceAllowed();
        long generation = _admission.Capture();
        PrepareScript();

        string outputPath = Path.Combine(Path.GetTempPath(), "parux-tts-" + Guid.NewGuid().ToString("N") + ".wav");
        string textArgPath = Path.Combine(Path.GetTempPath(), "parux-tts-text-" + Guid.NewGuid().ToString("N") + ".txt");
        string voice = SpeechVoiceSelector.Resolve(request.Voice, request.Text);
        if (voice.Length > 80 || voice.Contains('"') || voice.Any(char.IsControl))
            throw new InvalidDataException("voice contains unsupported characters.");

        try
        {
            await File.WriteAllTextAsync(textArgPath, request.Text, ct);
            JsonElement result;
            try
            {
                result = await GetWorker("synthesize").ExecuteAsync(new
                {
                    textPath = textArgPath,
                    voice,
                    outputPath
                }, ct, TimeSpan.FromMinutes(3), generation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                EnsureInferenceAllowed();
                _admission.Check(generation);
                result = await RunSynthesizeOneShotAsync(textArgPath, voice, outputPath, ct, generation).ConfigureAwait(false);
            }

            if (!File.Exists(outputPath)) throw new InvalidOperationException("The speech engine produced no output file.");
            byte[] bytes = await File.ReadAllBytesAsync(outputPath, ct);
            return new SynthesizeResponse
            {
                FileName = "speech_" + Guid.NewGuid().ToString("N")[..8] + ".wav",
                MediaType = "audio/wav",
                Base64Data = Convert.ToBase64String(bytes),
                DurationSeconds = result.TryGetProperty("duration", out JsonElement dur) ? dur.GetDouble() : 0
            };
        }
        finally
        {
            try { File.Delete(textArgPath); } catch { }
            try { File.Delete(outputPath); } catch { }
        }
    }

    /// <summary>Gaming Guard calls this before the global runtime shutdown. Worker objects remain
    /// reusable after gaming ends, but their Python children and resident model VRAM are gone.</summary>
    public bool StopWorkersForGaming()
    {
        // Close admission before draining workers: a delayed start can no longer
        // register a child after Gaming has inspected the existing process set.
        _admission.BlockForGaming();
        lock (_workerGate)
        {
            _sttWorker?.Stop();
            _ttsWorker?.Stop();
        }
        if (_runtime != null) ContinuousTtsWorker.For(_runtime).Stop();
        // A second bounded pass detects any still-owned child after individual
        // worker cleanup. False means shutdown is NOT confirmed, never "VRAM free".
        return _admission.BlockForGaming();
    }

    /// <summary>Reopen lazy speech admission only after the runtime leaves Gaming.
    /// Requests created before Gaming carry invalid generations and cannot revive.</summary>
    public void AllowWorkersAfterGaming() => _admission.ReopenAfterGaming();

    private async Task<JsonElement> RunTranscribeOneShotAsync(string audioPath, string language, CancellationToken ct, long generation)
    {
        (int exitCode, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
            "\"" + ScriptPath + "\" transcribe \"" + audioPath + "\" \"" + language + "\"", ct, TimeSpan.FromMinutes(3),
            admission: _admission, generation: generation);
        if (exitCode != 0) throw new InvalidOperationException("Transcription failed: " + Trim(stderr.Length > 0 ? stderr : stdout));
        return ParseLastJsonObject(stdout);
    }

    private async Task<JsonElement> RunSynthesizeOneShotAsync(string textPath, string voice, string outputPath, CancellationToken ct, long generation)
    {
        (int exitCode, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
            "\"" + ScriptPath + "\" synthesize \"" + textPath + "\" \"" + voice + "\" \"" + outputPath + "\"", ct, TimeSpan.FromMinutes(3),
            admission: _admission, generation: generation);
        if (exitCode != 0) throw new InvalidOperationException("Synthesis failed: " + Trim(stderr.Length > 0 ? stderr : stdout));
        return ParseLastJsonObject(stdout);
    }

    private SpeechWarmWorker GetWorker(string mode)
    {
        lock (_workerGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpeechService));
            SpeechWarmWorker Create() => _workerFactory?.Invoke(mode)
                ?? new SpeechWarmWorker(mode, VenvPython, ScriptPath, _admission, _gamingToken);
            return mode == "transcribe" ? _sttWorker ??= Create() : _ttsWorker ??= Create();
        }
    }

    private void EnsureInferenceAllowed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SpeechService));
        if (_gamingActive() || _admission.IsClosed)
            throw new SpeechUnavailableDuringGamingException("Gaming Mode is active, so local speech inference is unavailable.");
        if (!_engineInstalled())
            throw new InvalidOperationException("The speech engine is not installed on this machine yet.");
    }

    /// <summary>True when a failure belongs to a Gaming transition rather than to the caller or the engine.</summary>
    private bool IsGamingTransition(Exception ex, CancellationToken callerToken, long generation)
    {
        if (ex is SpeechUnavailableDuringGamingException) return true;
        if (callerToken.IsCancellationRequested || _disposed) return false;
        return _gamingActive() || _gamingToken().IsCancellationRequested
            || _admission.IsClosed || !_admission.IsCurrent(generation);
    }

    private static SpeechUnavailableDuringGamingException GamingInterrupted(Exception inner) =>
        new("Gaming Mode is active, so local speech is unavailable; the speech request was cancelled and its workers stopped.", inner);

    private void PrepareScript()
    {
        if (_workerFactory == null) WriteScript();
    }

    private static JsonElement ParseLastJsonObject(string output)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string candidate = line.Trim();
            if (!candidate.StartsWith('{') || !candidate.EndsWith('}')) continue;
            try { return JsonDocument.Parse(candidate).RootElement.Clone(); } catch (JsonException) { }
        }
        throw new InvalidDataException("The speech engine returned an unreadable result.");
    }

    private static void WriteScript()
    {
        Directory.CreateDirectory(RuntimeRoot);
        File.WriteAllText(ScriptPath, ScriptSource);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string fileName, string arguments, CancellationToken ct, TimeSpan timeout,
        string? workingDirectory = null, SpeechProcessAdmission? admission = null, long? generation = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        ManagedPythonRuntime.ApplyEnvironment(psi);
        var process = new Process { StartInfo = psi };
        bool started = false;
        bool confirmedExited = false;
        try
        {
            if (admission != null) admission.StartOwned(process, generation ?? admission.Capture());
            else if (!process.Start()) throw new InvalidOperationException("The speech process could not start.");
            started = true;
            // Same kill-on-close job as every other PaRuX AI child: a Core crash cannot orphan a one-shot speech process.
            ChildProcessJob.Assign(process);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("The local speech operation timed out.");
            }
            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    confirmedExited = process.WaitForExit(1200);
                }
                catch { }
                if (confirmedExited) admission?.Unregister(process);
            }
            // On an unconfirmed exit, keep an admitted child handle in the ownership
            // ledger so Gaming retries can still kill/check that exact process.
            if (!started || confirmedExited || admission == null)
                process.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_workerGate)
        {
            if (_disposed) return;
            _disposed = true;
            _sttWorker?.Dispose();
            _ttsWorker?.Dispose();
            _sttWorker = null;
            _ttsWorker = null;
        }
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 800 ? value : value[..800] + "…";
    }

    private const string ScriptSource = @"
import contextlib, json, sys, time

mode = sys.argv[1]


def load_stt():
    with contextlib.redirect_stdout(sys.stderr):
        from transformers import pipeline
        return pipeline('automatic-speech-recognition', model='nvidia/parakeet-tdt-0.6b-v3', device=0)


def transcribe(asr, audio_path, language):
    start = time.monotonic()
    with contextlib.redirect_stdout(sys.stderr):
        result = asr(audio_path)
    elapsed = time.monotonic() - start
    text = result['text'] if isinstance(result, dict) else str(result)
    return {'text': text, 'language': language or '', 'duration': elapsed}


def load_tts_modules():
    with contextlib.redirect_stdout(sys.stderr):
        from kokoro import KPipeline
        import soundfile as sf
        import numpy as np
    return KPipeline, sf, np


def synthesize(KPipeline, sf, np, pipelines, text_path, voice, output_path):
    with open(text_path, encoding='utf-8') as f:
        text = f.read()
    lang_code = voice[0] if voice else 'a'
    if lang_code not in pipelines:
        with contextlib.redirect_stdout(sys.stderr):
            pipelines[lang_code] = KPipeline(lang_code=lang_code)
    start = time.monotonic()
    chunks = []
    with contextlib.redirect_stdout(sys.stderr):
        for _, _, audio in pipelines[lang_code](text, voice=voice):
            chunks.append(audio)
    full = np.concatenate(chunks) if chunks else np.zeros(0, dtype='float32')
    sf.write(output_path, full, 24000)
    # 'duration' is the audio length (SynthesizeResponse.DurationSeconds); generation time is separate.
    return {'duration': len(full) / 24000.0, 'generationSeconds': time.monotonic() - start}


if mode == 'transcribe':
    asr = load_stt()
    payload = transcribe(asr, sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else '')
    print(json.dumps(payload), flush=True)

elif mode == 'synthesize':
    KPipeline, sf, np = load_tts_modules()
    payload = synthesize(KPipeline, sf, np, {}, sys.argv[2], sys.argv[3], sys.argv[4])
    print(json.dumps(payload), flush=True)

elif mode == 'worker-transcribe':
    asr = load_stt()
    print(json.dumps({'ready': True}), flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            payload = transcribe(asr, request['audioPath'], request.get('language', ''))
            payload['ok'] = True
            print(json.dumps(payload), flush=True)
        except Exception as exc:
            print(json.dumps({'ok': False, 'error': str(exc)[:800]}), flush=True)

elif mode == 'worker-synthesize':
    KPipeline, sf, np = load_tts_modules()
    pipelines = {}
    print(json.dumps({'ready': True}), flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            payload = synthesize(KPipeline, sf, np, pipelines, request['textPath'], request['voice'], request['outputPath'])
            payload['ok'] = True
            print(json.dumps(payload), flush=True)
        except Exception as exc:
            print(json.dumps({'ok': False, 'error': str(exc)[:800]}), flush=True)

else:
    print(json.dumps({'error': 'unknown mode ' + mode}), flush=True)
    sys.exit(1)
";
}
