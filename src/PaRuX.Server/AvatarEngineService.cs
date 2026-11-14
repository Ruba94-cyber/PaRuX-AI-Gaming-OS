using System.Diagnostics;
using System.Globalization;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Local talking-avatar generation via LatentSync. Source, Python, uv and ffmpeg are all managed
/// by PaRuX under %LOCALAPPDATA%\PaRuX\ai-runtimes; no system Git/Python/uv/ffmpeg prerequisite
/// is accepted. The upstream source is pinned to an immutable Git commit.
/// </summary>
public sealed class AvatarEngineService
{
    private const string LatentSyncCommit = "a229c3948406bc2cf6eaf4873e662e70c6a04746";
    private readonly SpeechService _speech;
    private readonly GamingChildScope? _scope;

    public AvatarEngineService(SpeechService speech, LocalRuntimeService? runtime = null)
    {
        _speech = speech;
        // Gaming hard-off: the LatentSync / ffmpeg / python children are owned by one scope that the Gaming guard stops
        // (and that refuses new children while Gaming is active). Without a runtime (tests) the ambient scope applies.
        if (runtime != null) _scope = new GamingChildScope("avatar", () => runtime.GamingMode, runtime.HardOff);
    }

    /// <summary>Test seam: an injected Gaming scope.</summary>
    internal AvatarEngineService(SpeechService speech, GamingChildScope scope)
    {
        _speech = speech;
        _scope = scope;
    }

    private static string RuntimeRoot => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "avatar");
    private static string RepoRoot => Path.Combine(RuntimeRoot, "LatentSync");
    private static string VenvRoot => Path.Combine(RepoRoot, ".venv");
    private static string VenvPython => ManagedPythonRuntime.VenvPython(VenvRoot);
    private static string CheckpointsDir => Path.Combine(RepoRoot, "checkpoints");
    private static string DefaultPhotoPath => Path.Combine(RuntimeRoot, "default-avatar.jpg");
    private static string LastErrorPath => Path.Combine(RuntimeRoot, "last-install-error.txt");
    private static string LastErrorCodePath => Path.Combine(RuntimeRoot, "last-install-error-code.txt");

    /// <summary>The stable code of the last failed install ("build_tools_required"), or null.</summary>
    internal static string? ReadLastErrorCode()
    {
        try { return File.Exists(LastErrorCodePath) ? File.ReadAllText(LastErrorCodePath).Trim() : null; }
        catch (IOException) { return null; }
    }

    /// <summary>True while Gaming Mode has the avatar engine paused (admission closed).</summary>
    internal bool GamingPaused => _scope?.IsClosed == true;

    public bool HasDefaultPhoto => File.Exists(DefaultPhotoPath);

    public async Task<AvatarEngineStatus> GetStatusAsync(CancellationToken ct)
    {
        // The engine check imports torch in a child; it is never started during Gaming (nothing to kill, nothing leaks).
        if (_scope?.IsClosed == true)
            return new AvatarEngineStatus
            {
                Installed = false,
                HasDefaultPhoto = HasDefaultPhoto,
                LastError = "Gaming Mode is active; the local avatar engine is paused and was not checked."
            };
        string? lastError = File.Exists(LastErrorPath) ? await File.ReadAllTextAsync(LastErrorPath, ct) : null;
        bool checkpointsPresent = File.Exists(Path.Combine(CheckpointsDir, "latentsync_unet.pt"))
            && File.Exists(Path.Combine(CheckpointsDir, "whisper", "tiny.pt"));
        if (!File.Exists(VenvPython) || !checkpointsPresent)
            return new AvatarEngineStatus { Installed = false, HasDefaultPhoto = HasDefaultPhoto, LastError = lastError, LastErrorCode = lastError == null ? null : ReadLastErrorCode() };

        try
        {
            (int exitCode, _, _) = await RunCapturedAsync(VenvPython,
                "-c \"import torch, diffusers, transformers, cv2, insightface, imageio_ffmpeg\"",
                ct, TimeSpan.FromSeconds(60), workingDirectory: RepoRoot);
            return new AvatarEngineStatus { Installed = exitCode == 0, HasDefaultPhoto = HasDefaultPhoto, LastError = exitCode == 0 ? null : lastError, LastErrorCode = exitCode == 0 || lastError == null ? null : ReadLastErrorCode() };
        }
        catch (UnavailableDuringGamingException)
        {
            return new AvatarEngineStatus
            {
                Installed = false,
                HasDefaultPhoto = HasDefaultPhoto,
                LastError = "Gaming Mode is active; the local avatar engine is paused and was not checked."
            };
        }
        catch
        {
            return new AvatarEngineStatus { Installed = false, HasDefaultPhoto = HasDefaultPhoto, LastError = lastError, LastErrorCode = lastError == null ? null : ReadLastErrorCode() };
        }
    }

    public Task<string> InstallEngineAsync(CancellationToken ct) =>
        GamingScoped(ct, linked => InstallEngineCoreAsync(linked));

    /// <summary>Runs under the avatar Gaming scope when there is one: a Gaming start cancels the work and the caller gets
    /// the typed "unavailable during Gaming" error, never a generic failure.</summary>
    private Task<T> GamingScoped<T>(CancellationToken ct, Func<CancellationToken, Task<T>> body) =>
        _scope == null ? body(ct) : _scope.RunAsync(ct, (linked, _) => body(linked));

    private async Task<string> InstallEngineCoreAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeRoot);
        var log = new System.Text.StringBuilder();
        try
        {
            TorchHardwareProfile torchHardware = await TorchInstallHardwareDetector.DetectAsync(ct).ConfigureAwait(false);
            TorchInstallChannelPlan torchPlan = TorchInstallChannelPolicy.Select(torchHardware);
            if (!torchPlan.CanInstall) throw new InvalidOperationException(torchPlan.Reason);

            await ManagedSourceArchive.EnsureGithubCommitAsync(
                "bytedance", "LatentSync", LatentSyncCommit, RepoRoot, ct).ConfigureAwait(false);
            await ManagedPythonRuntime.EnsureVenvAsync(VenvRoot, ManagedPythonRuntime.DefaultPythonVersion, ct)
                .ConfigureAwait(false);

            log.AppendLine(torchPlan.Reason);
            log.Append(await ManagedPythonRuntime.PipInstallAsync(
                VenvRoot,
                torchPlan.PipArguments,
                ct, TimeSpan.FromMinutes(15)).ConfigureAwait(false));

            // SEC-F02: the LatentSync requirement closure (resolved from the pinned commit's requirements.txt)
            // is installed only from the shipped hash lock, never from the downloaded requirements file.
            log.Append(await ManagedPythonRuntime.PipInstallAsync(
                VenvRoot, ManagedRequirementLocks.ArgumentsFor("avatar"),
                ct, TimeSpan.FromMinutes(20)).ConfigureAwait(false));

            Directory.CreateDirectory(Path.Combine(CheckpointsDir, "whisper"));
            // SEC-F02: both checkpoints are pickle files, so they are fetched from an immutable repository commit
            // and published only after the pinned size and SHA-256 match (Hugging Face LFS metadata of that commit).
            await DownloadIfMissingAsync(
                CheckpointUrl("whisper/tiny.pt"),
                Path.Combine(CheckpointsDir, "whisper", "tiny.pt"), WhisperTinySha256, WhisperTinyBytes, ct);
            await DownloadIfMissingAsync(
                CheckpointUrl("latentsync_unet.pt"),
                Path.Combine(CheckpointsDir, "latentsync_unet.pt"), UnetSha256, UnetBytes, ct);

            try { File.Delete(LastErrorPath); } catch { }
            try { File.Delete(LastErrorCodePath); } catch { }
            return log.ToString();
        }
        catch (Exception ex) when (ex is not (UnavailableDuringGamingException or OperationCanceledException))
        {
            // A Gaming interruption or a cancel is not an install defect and must not become "last install error".
            string? errorCode = ManagedInstallFailureCode.Of(ex);
            string detail = errorCode != null ? ex.Message : Trim(ex.Message);
            try { await File.WriteAllTextAsync(LastErrorPath, detail, ct); } catch { }
            // The code lets every UI show a localized state ("C++ build tools required", "data folder path too long"); other failures carry none.
            try
            {
                if (errorCode != null) await File.WriteAllTextAsync(LastErrorCodePath, errorCode, ct);
                else File.Delete(LastErrorCodePath);
            }
            catch { }
            throw;
        }
    }

    public async Task SetDefaultPhotoAsync(byte[] photoBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeRoot);
        await File.WriteAllBytesAsync(DefaultPhotoPath, photoBytes, ct);
    }

    public Task<byte[]> GenerateAsync(string text, CancellationToken ct) =>
        GamingScoped(ct, linked => GenerateCoreAsync(text, linked));

    private async Task<byte[]> GenerateCoreAsync(string text, CancellationToken ct)
    {
        if (!HasDefaultPhoto) throw new InvalidOperationException("No reference photo has been set for the local avatar engine yet.");
        if (!File.Exists(VenvPython)) throw new InvalidOperationException("The local avatar engine is not installed on this machine yet.");
        string ffmpeg = await ResolveManagedFfmpegAsync(ct).ConfigureAwait(false);

        SynthesizeResponse tts = await _speech.SynthesizeAsync(new SynthesizeRequest { Text = text, Voice = string.Empty }, ct);
        string audioExtension = tts.MediaType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".wav";
        string audioPath = Path.Combine(Path.GetTempPath(), "parux-avatar-audio-" + Guid.NewGuid().ToString("N") + audioExtension);
        string sourceVideoPath = Path.Combine(Path.GetTempPath(), "parux-avatar-src-" + Guid.NewGuid().ToString("N") + ".mp4");
        string outputVideoPath = Path.Combine(Path.GetTempPath(), "parux-avatar-out-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await File.WriteAllBytesAsync(audioPath, Convert.FromBase64String(tts.Base64Data), ct);
            double durationSeconds = tts.DurationSeconds > 0 ? tts.DurationSeconds : 5;

            (int loopExit, _, string loopErr) = await RunCapturedAsync(ffmpeg,
                "-hide_banner -loglevel error -y -loop 1 -i \"" + DefaultPhotoPath + "\" -t "
                + (durationSeconds + 1).ToString("0.00", CultureInfo.InvariantCulture)
                + " -r 25 -pix_fmt yuv420p \"" + sourceVideoPath + "\"",
                ct, TimeSpan.FromMinutes(2));
            if (loopExit != 0 || !File.Exists(sourceVideoPath))
                throw new InvalidOperationException("Couldn't turn the reference photo into a video: " + loopErr);

            string args = "-m scripts.inference"
                + " --unet_config_path \"configs/unet/stage2_512.yaml\""
                + " --inference_ckpt_path \"checkpoints/latentsync_unet.pt\""
                + " --inference_steps 20 --guidance_scale 1.5"
                + " --video_path \"" + sourceVideoPath + "\""
                + " --audio_path \"" + audioPath + "\""
                + " --video_out_path \"" + outputVideoPath + "\"";
            (int exitCode, string stdout, string stderr) = await RunCapturedAsync(
                VenvPython, args, ct, TimeSpan.FromMinutes(30), workingDirectory: RepoRoot);
            if (exitCode != 0 || !File.Exists(outputVideoPath))
            {
                string detail = Trim(stderr.Length > 0 ? stderr : stdout);
                try { await File.WriteAllTextAsync(LastErrorPath, detail, ct); } catch { }
                throw new InvalidOperationException("Avatar generation failed: " + detail);
            }
            return await File.ReadAllBytesAsync(outputVideoPath, ct);
        }
        finally
        {
            try { File.Delete(audioPath); } catch { }
            try { File.Delete(sourceVideoPath); } catch { }
            try { File.Delete(outputVideoPath); } catch { }
        }
    }

    private async Task<string> ResolveManagedFfmpegAsync(CancellationToken ct)
    {
        (int exit, string stdout, string stderr) = await RunCapturedAsync(VenvPython,
            "-c \"import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())\"",
            ct, TimeSpan.FromSeconds(30), workingDirectory: RepoRoot);
        string path = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        if (exit != 0 || !File.Exists(path))
            throw new InvalidOperationException("The PaRuX-managed ffmpeg runtime is unavailable: " + Trim(stderr));
        return path;
    }

    internal const string CheckpointRepositoryCommit = "c42c7e6c8e9c213626389fa7d9a3c444b8536353";
    internal const string WhisperTinySha256 = "65147644a518d12f04e32d6f3b26facc3f8dd46e5390956a9424a650c0ce22b9";
    internal const long WhisperTinyBytes = 75572083;
    internal const string UnetSha256 = "0a478e89eb660f82da4c35dbdde8a5adfb27f99d1b4e50edd03729e1e98316d3";
    internal const long UnetBytes = 5072222488;

    internal static string CheckpointUrl(string repositoryPath) =>
        "https://huggingface.co/ByteDance/LatentSync-1.6/resolve/" + CheckpointRepositoryCommit + "/" + repositoryPath;

    private static async Task DownloadIfMissingAsync(
        string url, string destination, string sha256, long sizeBytes, CancellationToken ct)
    {
        if (File.Exists(destination)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        await VerifiedDownload.DownloadAsync(http, url, destination, sha256, sizeBytes, ct).ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string fileName, string arguments, CancellationToken ct, TimeSpan timeout, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
        using var process = new Process { StartInfo = psi };
        using GamingChildLease gaming = GamingChildGate.StartOwned(process, ct, _scope);
        ChildProcessJob.Assign(process);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(gaming.Token);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            gaming.ThrowIfGamingInterrupted();
            throw;
        }
        if (process.ExitCode != 0) gaming.ThrowIfGamingInterrupted();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Trim(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 2000 ? value : "…" + value[^2000..];
    }
}
