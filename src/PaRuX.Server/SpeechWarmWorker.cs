using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>
/// Owns one long-lived PaRuX-managed Python speech process. Requests are JSONL framed and
/// serialized because the underlying GPU pipelines are not assumed to be re-entrant. A protocol,
/// process or cancellation failure tears the worker down; SpeechService can then fail-soft to its
/// one-shot execution path and the next request will attempt a clean warm-worker restart.
/// </summary>
internal sealed class SpeechWarmWorker : IDisposable
{
    private readonly string _mode;
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly SpeechProcessAdmission _admission;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Process? _process;
    private readonly StringBuilder _stderr = new();
    private bool _disposed;

    private readonly Func<CancellationToken> _gamingToken;
    private readonly Func<ProcessStartInfo>? _startInfoForTests;

    public SpeechWarmWorker(string mode, string pythonPath, string scriptPath, SpeechProcessAdmission? admission = null,
        Func<CancellationToken>? gamingToken = null, Func<ProcessStartInfo>? startInfoForTests = null)
    {
        if (mode is not ("transcribe" or "synthesize"))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        _pythonPath = pythonPath;
        _scriptPath = scriptPath;
        _admission = admission ?? new SpeechProcessAdmission(() => false);
        _gamingToken = gamingToken ?? (() => CancellationToken.None);
        _startInfoForTests = startInfoForTests;
    }

    public async Task<JsonElement> ExecuteAsync(object request, CancellationToken ct, TimeSpan timeout, long? admittedGeneration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long generation = admittedGeneration ?? _admission.Capture();
        // The Gaming token cancels an in-flight request the moment the runtime flips, without waiting for the
        // guard's next reconciliation step to kill the child.
        CancellationToken gaming = _gamingToken();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct, gaming);
        try
        {
            await _gate.WaitAsync(requestCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsGamingFailure(ex, ct, gaming, generation))
        {
            throw GamingUnavailable(ex);
        }
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await EnsureStartedAsync(timeoutCts.Token, generation).ConfigureAwait(false);
                _admission.Check(generation);
                Process process = _process ?? throw new InvalidOperationException("Speech warm worker failed to start.");
                string command = JsonSerializer.Serialize(request);
                await process.StandardInput.WriteLineAsync(command.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    throw new InvalidDataException("Speech warm worker closed its response stream unexpectedly.");

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
                {
                    string error = root.TryGetProperty("error", out JsonElement errorElement)
                        ? errorElement.GetString() ?? "unknown worker error"
                        : "invalid worker response";
                    throw new InvalidOperationException("Speech warm worker failed: " + Trim(error));
                }
                // A hard-off that landed while the answer was being read must not be reported as a normal result.
                _admission.Check(generation);
                return root.Clone();
            }
            catch (Exception ex)
            {
                StopProcess();
                if (IsGamingFailure(ex, ct, gaming, generation)) throw GamingUnavailable(ex);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PrewarmAsync(CancellationToken ct, TimeSpan timeout, long? admittedGeneration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long generation = admittedGeneration ?? _admission.Capture();
        CancellationToken gaming = _gamingToken();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct, gaming);
        try
        {
            await _gate.WaitAsync(requestCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsGamingFailure(ex, ct, gaming, generation))
        {
            throw GamingUnavailable(ex);
        }
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token);
            timeoutCts.CancelAfter(timeout);
            try { await EnsureStartedAsync(timeoutCts.Token, generation).ConfigureAwait(false); }
            catch (Exception ex)
            {
                StopProcess();
                if (IsGamingFailure(ex, ct, gaming, generation)) throw GamingUnavailable(ex);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>True when a failure is the consequence of a Gaming transition rather than a caller cancel or an
    /// engine fault: the Gaming token fired, admission is closed, or this request's ticket was invalidated.</summary>
    private bool IsGamingFailure(Exception ex, CancellationToken callerToken, CancellationToken gamingToken, long generation)
    {
        if (ex is SpeechUnavailableDuringGamingException) return true;
        if (callerToken.IsCancellationRequested) return false;
        return gamingToken.IsCancellationRequested || _admission.IsClosed || !_admission.IsCurrent(generation);
    }

    private static SpeechUnavailableDuringGamingException GamingUnavailable(Exception inner) =>
        new("Gaming Mode is active, so local speech is unavailable; the speech worker was stopped.", inner);

    /// <summary>Kills only the resident child process. The worker object remains reusable after
    /// Gaming Mode exits, so Gaming Guard can release speech VRAM immediately without disposing
    /// the singleton service or racing a SemaphoreSlim disposal against an in-flight request.</summary>
    public void Stop() => StopProcess();

    /// <summary>OS id of the resident child, or null when none is resident (diagnostics and tests).</summary>
    internal int? ChildProcessId
    {
        get
        {
            try { return _process?.Id; } catch { return null; }
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct, long generation)
    {
        _admission.Check(generation);
        if (_process is { HasExited: false }) return;
        StopProcess();

        ProcessStartInfo info;
        if (_startInfoForTests != null)
        {
            info = _startInfoForTests();
        }
        else
        {
            info = new ProcessStartInfo
            {
                FileName = _pythonPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_scriptPath) ?? AppContext.BaseDirectory
            };
            info.ArgumentList.Add(_scriptPath);
            info.ArgumentList.Add("worker-" + _mode);
            ManagedPythonRuntime.ApplyEnvironment(info);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, args) => AppendStderr(args.Data);
        bool registered = false;
        try
        {
            _admission.StartOwned(process, generation);
            registered = true;
            _process = process;
            ChildProcessJob.Assign(process);
            process.BeginErrorReadLine();
        }
        catch
        {
            // If process startup itself failed there is no owned child. Otherwise
            // let StopProcess retain an unconfirmed child in the admission ledger.
            if (registered) StopProcess();
            else process.Dispose();
            throw;
        }

        string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ready))
            throw new InvalidDataException("Speech warm worker exited before reporting readiness: " + Trim(ReadStderr()));
        using JsonDocument document = JsonDocument.Parse(ready);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("ready", out JsonElement readyElement) || !readyElement.GetBoolean())
            throw new InvalidDataException("Speech warm worker returned an invalid readiness frame: " + Trim(ready));
        // A spawn that completed after a hard-off began must not be left resident: BlockForGaming already
        // killed every registered child, and this ticket check turns the late readiness into a typed refusal.
        _admission.Check(generation);
    }

    private void AppendStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_stderr)
        {
            if (_stderr.Length > 6000) _stderr.Remove(0, Math.Min(3000, _stderr.Length));
            _stderr.AppendLine(line);
        }
    }

    private string ReadStderr()
    {
        lock (_stderr) return _stderr.ToString();
    }

    private void StopProcess()
    {
        Process? process = Interlocked.Exchange(ref _process, null);
        if (process == null) return;
        bool confirmed = false;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            confirmed = process.WaitForExit(1200);
        }
        catch { }
        // Never abandon an unconfirmed child: the admission ledger retains it for
        // retry and prevents a later generation from silently reclaiming speech.
        if (confirmed)
        {
            _admission.Unregister(process);
            try { process.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProcess();
        _gate.Dispose();
    }

    private static string Trim(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 800 ? value : value[..800] + "…";
    }
}
