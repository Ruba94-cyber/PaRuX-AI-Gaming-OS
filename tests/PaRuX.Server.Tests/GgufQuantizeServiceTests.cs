using System.Text;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GgufQuantizeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-quant-" + Guid.NewGuid().ToString("N"));
    private readonly string _quantizer;
    private readonly string _input;

    public GgufQuantizeServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _quantizer = Path.Combine(_dir, "llama-quantize.exe");
        File.WriteAllBytes(_quantizer, new byte[] { 0x4D, 0x5A });
        _input = Path.Combine(_dir, "model-f16.gguf");
        File.WriteAllBytes(_input, Header(4096));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Header(int length)
    {
        var data = new byte[length];
        Encoding.ASCII.GetBytes("GGUF").CopyTo(data, 0);
        return data;
    }

    private GgufQuantizeService Make(
        Func<GgufQuantizeService.ProcessSpec, CancellationToken, Task<(int, string)>> run,
        long free = long.MaxValue, bool installed = true, TimeSpan? timeout = null) =>
        new(() => installed ? _quantizer : null, run, _ => free, timeout ?? TimeSpan.FromMinutes(1));

    private static Task<(int, string)> WritesOutput(GgufQuantizeService.ProcessSpec spec, CancellationToken ct)
    {
        File.WriteAllBytes(spec.Arguments[1], Header(2048));
        return Task.FromResult((0, "ok"));
    }

    private string Out(string name = "model-q4.gguf") => Path.Combine(_dir, name);

    [Fact]
    public async Task Success_promotes_the_partial_and_writes_a_hash_manifest()
    {
        GgufQuantizeService.ProcessSpec? seen = null;
        var service = Make((spec, ct) => { seen = spec; return WritesOutput(spec, ct); });
        var result = await service.QuantizeAsync(new(_input, Out(), "q4_k_m"), CancellationToken.None);

        Assert.Equal("Q4_K_M", result.Type);
        Assert.Equal(new[] { _input, Out() + ".partial", "Q4_K_M" }, seen!.Value.Arguments);
        Assert.True(File.Exists(Out()));
        Assert.False(File.Exists(Out() + ".partial"));
        Assert.Equal(2048, result.OutputBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Out()))).ToLowerInvariant(), result.Sha256);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Out() + ".quant.json"));
        Assert.Equal(result.Sha256, manifest.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("model-f16.gguf", manifest.RootElement.GetProperty("source").GetString());
        Assert.Equal(4096, File.ReadAllBytes(_input).Length); // the source is never touched
    }

    [Fact]
    public async Task Changed_source_during_quantization_is_detected_before_output_publication()
    {
        var service = Make(async (spec, ct) =>
        {
            await WritesOutput(spec, ct);
            File.WriteAllBytes(_input, Header(8192)); // same GGUF magic, different source bytes
            return (0, "finished");
        });
        var failure = await Assert.ThrowsAsync<GgufQuantizeException>(() =>
            service.QuantizeAsync(new(_input, Out(), "Q4_K_M"), CancellationToken.None));
        Assert.Contains("changed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Out()));
        Assert.False(File.Exists(Out() + ".partial"));
        Assert.False(File.Exists(Out() + ".quant.json"));
        Assert.Equal(8192, new FileInfo(_input).Length);
    }

    [Fact]
    public async Task Manifest_records_original_source_sha256_for_reproducible_export()
    {
        var service = Make(WritesOutput);
        string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_input)))
            .ToLowerInvariant();
        await service.QuantizeAsync(new(_input, Out(), "Q4_K_M"), CancellationToken.None);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Out() + ".quant.json"));
        Assert.Equal(expected, manifest.RootElement.GetProperty("sourceSha256").GetString());
    }

    [Fact]
    public async Task Nonzero_exit_removes_the_partial_and_reports_the_tail()
    {
        var service = Make((spec, ct) =>
        {
            File.WriteAllBytes(spec.Arguments[1], Header(10));
            return Task.FromResult((3, "tensor blk.0 unsupported"));
        });
        var ex = await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
        Assert.Contains("exit 3", ex.Message);
        Assert.Contains("unsupported", ex.Message);
        Assert.False(File.Exists(Out() + ".partial"));
        Assert.False(File.Exists(Out()));
    }

    [Fact]
    public async Task Output_without_a_gguf_header_is_not_promoted()
    {
        var service = Make((spec, ct) => { File.WriteAllText(spec.Arguments[1], "not a model"); return Task.FromResult((0, "")); });
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
        Assert.False(File.Exists(Out()));
        Assert.False(File.Exists(Out() + ".partial"));
    }

    [Fact]
    public async Task Success_claim_without_an_output_file_is_a_failure()
    {
        var service = Make((spec, ct) => Task.FromResult((0, "")));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_cleans_up()
    {
        using var cts = new CancellationTokenSource();
        var service = Make(async (spec, ct) =>
        {
            File.WriteAllBytes(spec.Arguments[1], Header(10));
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return (0, "");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.QuantizeAsync(new(_input, Out(), "Q8_0"), cts.Token));
        Assert.False(File.Exists(Out() + ".partial"));
    }

    [Fact]
    public async Task Timeout_is_reported_as_a_clean_failure_not_a_cancellation()
    {
        var service = Make(async (spec, ct) => { await Task.Delay(Timeout.Infinite, ct); return (0, ""); },
            timeout: TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
        Assert.Contains("timed out", ex.Message);
    }

    [Theory]
    [InlineData("Q4_K_M; calc")]
    [InlineData("Q9_9")]
    [InlineData("")]
    [InlineData("--allow-requantize")]
    public async Task Unknown_types_and_flag_injection_are_rejected_before_anything_runs(string type)
    {
        bool ran = false;
        var service = Make((spec, ct) => { ran = true; return WritesOutput(spec, ct); });
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Out(), type), CancellationToken.None));
        Assert.False(ran);
    }

    [Fact]
    public async Task Path_rules_are_enforced()
    {
        var service = Make(WritesOutput);
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new("relative.gguf", Out(), "Q8_0"), CancellationToken.None));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, @"\\server\share\o.gguf", "Q8_0"), CancellationToken.None));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Out("o.bin"), "Q8_0"), CancellationToken.None));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, _input, "Q8_0"), CancellationToken.None));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(Out("missing.gguf"), Out(), "Q8_0"), CancellationToken.None));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Path.Combine(_dir, "no-such-dir", "o.gguf"), "Q8_0"), CancellationToken.None));
    }

    [Fact]
    public async Task Existing_output_is_never_overwritten()
    {
        File.WriteAllText(Out(), "precious");
        var service = Make(WritesOutput);
        await Assert.ThrowsAsync<GgufQuantizeException>(() => service.QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
        Assert.Equal("precious", File.ReadAllText(Out()));
    }

    [Theory]
    [InlineData(".partial")]
    [InlineData(".quant.json")]
    public async Task Existing_quantization_sidecar_is_never_deleted_or_overwritten(string suffix)
    {
        string owned = Out() + suffix;
        File.WriteAllText(owned, "precious original");
        bool ran = false;
        var service = Make((spec, ct) => { ran = true; return WritesOutput(spec, ct); });
        await Assert.ThrowsAsync<GgufQuantizeException>(() =>
            service.QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
        Assert.False(ran);
        Assert.Equal("precious original", File.ReadAllText(owned));
        Assert.False(File.Exists(Out()));
    }

    [Fact]
    public async Task Linked_input_cannot_be_quantized()
    {
        string alias = Path.Combine(_dir, "linked.gguf");
        try { File.CreateSymbolicLink(alias, _input); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // synthetic filesystem link unsupported on this host
        }
        bool ran = false;
        var service = Make((spec, ct) => { ran = true; return WritesOutput(spec, ct); });
        await Assert.ThrowsAsync<GgufQuantizeException>(() =>
            service.QuantizeAsync(new(alias, Out(), "Q8_0"), CancellationToken.None));
        Assert.False(ran);
        Assert.Equal(4096, new FileInfo(_input).Length);
    }

    [Fact]
    public async Task Output_under_linked_directory_is_rejected_without_mutating_target()
    {
        string target = Path.Combine(_dir, "real-output");
        Directory.CreateDirectory(target);
        string alias = Path.Combine(_dir, "linked-output");
        try { Directory.CreateSymbolicLink(alias, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }
        bool ran = false;
        var service = Make((spec, ct) => { ran = true; return WritesOutput(spec, ct); });
        await Assert.ThrowsAsync<GgufQuantizeException>(() =>
            service.QuantizeAsync(new(_input, Path.Combine(alias, "quant.gguf"), "Q8_0"), CancellationToken.None));
        Assert.False(ran);
        Assert.Empty(Directory.EnumerateFiles(target));
    }

    [Fact]
    public async Task Input_must_be_a_gguf_and_space_and_quantizer_must_exist()
    {
        string fake = Path.Combine(_dir, "fake.gguf");
        File.WriteAllText(fake, "hello world");
        await Assert.ThrowsAsync<GgufQuantizeException>(() => Make(WritesOutput).QuantizeAsync(new(fake, Out(), "Q8_0"), CancellationToken.None));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => Make(WritesOutput, free: 1000).QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
        await Assert.ThrowsAsync<GgufQuantizeException>(() => Make(WritesOutput, installed: false).QuantizeAsync(new(_input, Out(), "Q8_0"), CancellationToken.None));
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_report_status_and_can_be_cancelled()
    {
        var gate = new TaskCompletionSource();
        var service = Make(async (spec, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return await WritesOutput(spec, ct);
        });
        var jobs = new GgufQuantizeJobs(service, () => false);
        var started = jobs.Start(new(_input, Out(), "Q8_0"));
        Assert.Equal("running", started.State);
        Assert.Throws<InvalidOperationException>(() => jobs.Start(new(_input, Out("other.gguf"), "Q8_0")));

        Assert.True(jobs.Cancel());
        await jobs.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", jobs.Current!.State);
        Assert.False(jobs.Cancel());

        gate.SetResult();
        var second = jobs.Start(new(_input, Out(), "Q8_0"));
        await jobs.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", jobs.Current!.State);
        Assert.NotEqual(started.Id, second.Id);
        Assert.NotNull(jobs.Current.Result);
    }

    [Fact]
    public async Task External_gaming_marker_cancels_a_running_quantization_without_an_explicit_stop_request()
    {
        int gaming = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Make(async (spec, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return (0, "");
        });
        var jobs = new GgufQuantizeJobs(service, () => Volatile.Read(ref gaming) != 0);
        jobs.Start(new(_input, Out(), "Q8_0"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Interlocked.Exchange(ref gaming, 1);
        await jobs.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", jobs.Current!.State);
        Assert.False(File.Exists(Out()));
        Assert.Throws<InvalidOperationException>(() => jobs.Start(new(_input, Out(), "Q8_0")));
    }

    [Fact]
    public async Task Gaming_stop_waits_for_cancelled_quantization_before_allowing_a_game()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Make(async (spec, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return (0, "");
        });
        var jobs = new GgufQuantizeJobs(service, () => false);
        jobs.Start(new(_input, Out(), "Q8_0"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await jobs.StopForGamingAsync(CancellationToken.None, TimeSpan.FromSeconds(5)));
        Assert.Equal("cancelled", jobs.Current!.State);
        Assert.False(File.Exists(Out()));
        Assert.False(File.Exists(Out() + ".partial"));
    }

    [Fact]
    public async Task Gaming_refuses_to_enter_when_quantizer_ignores_cancel_until_it_releases_the_job()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Make(async (spec, ct) =>
        {
            entered.TrySetResult();
            await release.Task; // deliberately ignores the cancellation token
            return await WritesOutput(spec, ct);
        });
        var jobs = new GgufQuantizeJobs(service, () => false);
        jobs.Start(new(_input, Out(), "Q8_0"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(await jobs.StopForGamingAsync(CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.Equal("running", jobs.Current!.State);
        release.TrySetResult();
        await jobs.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", jobs.Current!.State);
        Assert.False(File.Exists(Out())); // a cancelled runner must not publish a GGUF
        Assert.False(File.Exists(Out() + ".partial"));
        Assert.True(await jobs.StopForGamingAsync(CancellationToken.None));
    }

    [Fact]
    public void Jobs_refuse_to_start_while_gaming_or_without_a_quantizer()
    {
        Assert.Throws<InvalidOperationException>(() => new GgufQuantizeJobs(Make(WritesOutput), () => true).Start(new(_input, Out(), "Q8_0")));
        Assert.Throws<InvalidOperationException>(() => new GgufQuantizeJobs(Make(WritesOutput, installed: false), () => false).Start(new(_input, Out(), "Q8_0")));
    }
}
