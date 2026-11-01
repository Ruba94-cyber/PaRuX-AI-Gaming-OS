using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

public sealed record GgufQuantizeRequest(string InputPath, string OutputPath, string Type);

public sealed record GgufQuantizeResult(string OutputPath, long OutputBytes, string Sha256, string Type, string Quantizer);

/// <summary>A failure whose message is safe to show to the user (no paths beyond the ones they supplied).</summary>
public sealed class GgufQuantizeException : Exception
{
    public bool ShutdownUnconfirmed { get; }
    public GgufQuantizeException(string message, bool shutdownUnconfirmed = false) : base(message)
        => ShutdownUnconfirmed = shutdownUnconfirmed;
}

/// <summary>
/// CPU quantization of a local GGUF through the llama.cpp <c>llama-quantize</c> that ships with the managed
/// runtime (Unsloth-style "export a smaller model" step, no Python and no GPU). Safety model: no shell, never
/// overwrites a file, writes a <c>.partial</c> beside the target and promotes it only after the process
/// succeeded and the result carries a GGUF header, kills the whole process tree on cancel or timeout, and
/// writes a SHA-256 manifest next to the output. It does not judge model quality.
/// </summary>
public sealed class GgufQuantizeService
{
    public static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_0", "Q4_K_M", "Q4_K_S", "Q4_0", "Q3_K_M", "Q3_K_S", "Q3_K_L", "Q2_K",
        "IQ4_XS", "F16", "BF16",
    };

    internal readonly record struct ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

    private const long MinFreeMarginBytes = 256L * 1024 * 1024;
    private static readonly byte[] GgufMagic = Encoding.ASCII.GetBytes("GGUF");

    private readonly Func<string?> _locateQuantizer;
    private readonly Func<ProcessSpec, CancellationToken, Task<(int ExitCode, string Tail)>> _run;
    private readonly Func<string, long> _freeBytes;
    private readonly TimeSpan _timeout;

    public GgufQuantizeService()
        : this(DefaultLocate, RunProcessAsync, DefaultFreeBytes, TimeSpan.FromHours(6)) { }

    internal GgufQuantizeService(
        Func<string?> locateQuantizer,
        Func<ProcessSpec, CancellationToken, Task<(int ExitCode, string Tail)>> run,
        Func<string, long> freeBytes,
        TimeSpan timeout)
    {
        _locateQuantizer = locateQuantizer;
        _run = run;
        _freeBytes = freeBytes;
        _timeout = timeout;
    }

    public bool IsAvailable => _locateQuantizer() is not null;

    public async Task<GgufQuantizeResult> QuantizeAsync(GgufQuantizeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string type = (request.Type ?? string.Empty).Trim().ToUpperInvariant();
        if (!AllowedTypes.Contains(type)) throw new GgufQuantizeException("That quantization type is not supported.");

        string input = ValidatePath(request.InputPath, "input");
        string output = ValidatePath(request.OutputPath, "output");
        if (!File.Exists(input)) throw new GgufQuantizeException("The input GGUF does not exist.");
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new GgufQuantizeException("The output must be a different file from the input.");
        if (PathOccupied(output)) throw new GgufQuantizeException("The output file already exists; it is never overwritten.");
        string? outputDirectory = Path.GetDirectoryName(output);
        if (outputDirectory is null || !Directory.Exists(outputDirectory))
            throw new GgufQuantizeException("The output folder does not exist.");
        if (!HasGgufHeader(input)) throw new GgufQuantizeException("The input is not a GGUF file.");

        long inputBytes = new FileInfo(input).Length;
        if (_freeBytes(outputDirectory) < inputBytes + MinFreeMarginBytes)
            throw new GgufQuantizeException("Not enough free disk space for the quantized file.");

        string quantizer = _locateQuantizer()
            ?? throw new GgufQuantizeException("llama-quantize is not installed in the managed runtime.");
        // Preserve source provenance and detect in-place modification while quantizing.
        // Hashing may add disk I/O; it is intentionally restricted to an explicit export job.
        string inputSha256 = await HashAsync(input, ct).ConfigureAwait(false);

        string partial = output + ".partial";
        string manifestPath = output + ".quant.json";
        // A previous interrupted attempt or another tool may own these files. Never delete or
        // replace existing sidecars, including dangling links and directories with these names.
        if (PathOccupied(partial) || PathOccupied(manifestPath))
            throw new GgufQuantizeException("A partial output or quantization manifest already exists. Move it aside before retrying.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            var spec = new ProcessSpec(quantizer, new[] { input, partial, type }, Path.GetDirectoryName(quantizer)!);
            (int exit, string tail) = await _run(spec, timeout.Token).ConfigureAwait(false);
            // A third-party quantizer or injected runner may ignore cancellation.
            // Never publish its output when Gaming has already cancelled the job.
            timeout.Token.ThrowIfCancellationRequested();
            if (exit != 0)
                throw new GgufQuantizeException($"llama-quantize failed (exit {exit}). {Tail(tail)}".TrimEnd());
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0 || !HasGgufHeader(partial))
                throw new GgufQuantizeException("llama-quantize finished but did not produce a valid GGUF file.");

            string currentSourceHash;
            try { currentSourceHash = await HashAsync(input, ct).ConfigureAwait(false); }
            catch (IOException) { throw new GgufQuantizeException("The source GGUF became unavailable during quantization."); }
            if (!string.Equals(currentSourceHash, inputSha256, StringComparison.Ordinal))
                throw new GgufQuantizeException("The source GGUF changed during quantization. No output was published.");

            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, output); // never overwrites: the target was checked and Move without overwrite throws
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(partial);
            throw new GgufQuantizeException("Quantization timed out and was stopped.");
        }
        catch (GgufQuantizeException ex) when (ex.ShutdownUnconfirmed)
        {
            // An unconfirmed external process may still have an open handle on
            // the partial output. Do not delete/replace it while the process may
            // still be running. Keep Gaming admission blocked until remediation.
            throw;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        string sha256 = await HashAsync(output, ct).ConfigureAwait(false);
        long outputBytes = new FileInfo(output).Length;
        string quantizerName = Path.GetFileName(quantizer) + " " +
            (FileVersionInfo.GetVersionInfo(quantizer).FileVersion ?? "unknown-version");
        var manifest = new
        {
            schema = 1,
            source = Path.GetFileName(input),
            sourceBytes = inputBytes,
            sourceSha256 = inputSha256,
            type,
            output = Path.GetFileName(output),
            outputBytes,
            sha256,
            quantizer = quantizerName,
            createdUtc = DateTime.UtcNow,
        };
        // An output created by this run must never overwrite a manifest owned by another run.
        // The GGUF is retained if manifest creation fails: the user's computed model is not deleted.
        using (var manifestStream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(manifestStream, new UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return new GgufQuantizeResult(output, outputBytes, sha256, type, quantizerName);
    }

    private static string ValidatePath(string? path, string role)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new GgufQuantizeException($"The {role} path must be an absolute local path.");
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new GgufQuantizeException($"The {role} file must be a .gguf file.");
        // Local-only files and folders: do not follow a symlink/junction into another
        // location, even when a nested leaf appears to be a regular file.
        try
        {
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                throw new GgufQuantizeException($"The {role} path has no local volume.");
            DriveType drive = new DriveInfo(root).DriveType;
            if (drive is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                throw new GgufQuantizeException($"The {role} must be on a local volume.");
            for (string? folder = Path.GetDirectoryName(full); folder != null; folder = Path.GetDirectoryName(folder))
            {
                if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new GgufQuantizeException($"The {role} path contains a linked folder.");
            }
            if ((File.Exists(full) || Directory.Exists(full)) &&
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new GgufQuantizeException($"The {role} path is a linked file.");
            if (new FileInfo(full).LinkTarget is not null || new DirectoryInfo(full).LinkTarget is not null)
                throw new GgufQuantizeException($"The {role} path is linked.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new GgufQuantizeException($"The {role} path could not be verified as local and unlinked.");
        }
        return full;
    }

    private static bool PathOccupied(string path) =>
        File.Exists(path) || Directory.Exists(path) ||
        new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    internal static bool HasGgufHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[4];
            return stream.Read(buffer, 0, 4) == 4 && buffer.AsSpan().SequenceEqual(GgufMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string Tail(string text) => text.Length <= 400 ? text.Trim() : "…" + text[^400..].Trim();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string? DefaultLocate()
    {
        foreach (string folder in new[] { LocalAiPaths.RuntimeCudaPath, LocalAiPaths.RuntimeVulkanPath })
        {
            string candidate = Path.Combine(folder, OperatingSystem.IsWindows() ? "llama-quantize.exe" : "llama-quantize");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static long DefaultFreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return 0; }
    }

    private static async Task<(int ExitCode, string Tail)> RunProcessAsync(ProcessSpec spec, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in spec.Arguments) info.ArgumentList.Add(argument);

        var tail = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (tail)
            {
                tail.AppendLine(line);
                if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
            }
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try { process.Start(); }
        catch (Win32Exception) { throw new GgufQuantizeException("llama-quantize could not be started."); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            // Kill requests are not completion evidence. Verify the root process has
            // exited before unblocking Gaming; detached grandchildren require PC tests.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new GgufQuantizeException(
                    "The quantizer did not confirm process exit after cancellation.",
                    shutdownUnconfirmed: true);
            }
            throw;
        }
        process.WaitForExit(); // flush the redirected streams
        lock (tail) return (process.ExitCode, tail.ToString());
    }
}
