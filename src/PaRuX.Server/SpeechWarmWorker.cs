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
    private Process? _process;
    private readonly StringBuilder _stderr = new();
    private bool _disposed;

    public SpeechWarmWorker(string mode, string pythonPath, string scriptPath, SpeechProcessAdmission? admission = null)
    {
        if (mode is not ("transcribe" or "synthesize"))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        _pythonPath = pythonPath;
        _scriptPath = scriptPath;
        _admission = admission ?? new SpeechProcessAdmission(() => false);
    }

    public async Task<JsonElement> ExecuteAsync(object request, CancellationToken ct, TimeSpan timeout, long? admittedGeneration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long generation = admittedGeneration ?? _admission.Capture();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await EnsureStartedAsync(timeoutCts.Token, generation).ConfigureAwait(false);
                _admission.Check(generation);
                Process process = _process ?? throw new InvalidOperationException("Speech warm worker failed to start.");
                string command = JsonSerializer.Serialize(request);
                await process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);

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
                return root.Clone();
            }
            catch
            {
                StopProcess();
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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { await EnsureStartedAsync(timeoutCts.Token, generation).ConfigureAwait(false); }
            catch { StopProcess(); throw; }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Kills only the resident child process. The worker object remains reusable after
    /// Gaming Mode exits, so Gaming Guard can release speech VRAM immediately without disposing
    /// the singleton service or racing a SemaphoreSlim disposal against an in-flight request.</summary>
    public void Stop() => StopProcess();

    private async Task EnsureStartedAsync(CancellationToken ct, long generation)
    {
        _admission.Check(generation);
        if (_process is { HasExited: false }) return;
        StopProcess();

        var info = new ProcessStartInfo
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

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, args) => AppendStderr(args.Data);
        try
        {
            _admission.StartOwned(process, generation);
            _process = process;
            ChildProcessJob.Assign(process);
            process.BeginErrorReadLine();
        }
        catch
        {
            _admission.Unregister(process);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            _process = null;
            throw;
        }

        string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ready))
            throw new InvalidDataException("Speech warm worker exited before reporting readiness: " + Trim(ReadStderr()));
        using JsonDocument document = JsonDocument.Parse(ready);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("ready", out JsonElement readyElement) || !readyElement.GetBoolean())
            throw new InvalidDataException("Speech warm worker returned an invalid readiness frame: " + Trim(ready));
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
        _admission.Unregister(process);
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process.Dispose(); } catch { }
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
