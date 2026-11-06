using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Hermetic, real-file checks for the complete Quick Training approval path.
/// No GPU/model/runtime is loaded by these tests.
/// </summary>
public sealed class QuickTrainingDatasetValidatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parux-quick-full-" + Guid.NewGuid().ToString("N"));

    private string Write(string file, IEnumerable<string> records)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, file);
        File.WriteAllLines(path, records, new UTF8Encoding(false));
        return path;
    }

    private static string Chat(int n) => JsonSerializer.Serialize(new
    {
        messages = new[]
        {
            new { role = "user", content = "Question " + n },
            new { role = "assistant", content = "Answer " + n }
        }
    });

    private static string Preference(int n) => JsonSerializer.Serialize(new
    {
        prompt = "Q" + n, chosen = "preferred " + n, rejected = "rejected " + n
    });

    [Theory]
    [InlineData("sft")]
    [InlineData("dpo")]
    public async Task Files_above_two_thousand_records_pass_complete_validation_and_produce_exact_hash(string method)
    {
        const int count = 2_345;
        string path = Write(method + ".jsonl", Enumerable.Range(0, count)
            .Select(n => method == TrainingMethod.Sft ? Chat(n) : Preference(n)));
        Assert.True(TrainingDatasetShape.Inspect(path).Truncated);

        var verified = await QuickTrainingDatasetValidator.ValidateAsync(
            path, method, CancellationToken.None);
        Assert.Equal(count, verified.Records);
        Assert.Equal(new FileInfo(path).Length, verified.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            verified.Sha256);
        Assert.Null(QuickTrainingPolicy.RefusalAfterFullVerification(
            true, true, verified.Records, verified.Sha256));
        TrainingLabService.VerifyExpectedDatasetHash(verified.Sha256,
            TrainingJobDurability.ComputeFileSha256(path));

        // Additive request fields travel through the same JSON API as desktop/CLI.
        var roundTrip = JsonSerializer.Deserialize<TrainingJobRequest>(
            JsonSerializer.Serialize(new TrainingJobRequest
            {
                TrainJsonlPath = path,
                ExpectedDatasetSha256 = verified.Sha256,
                Method = method
            }));
        Assert.Equal(verified.Sha256, roundTrip!.ExpectedDatasetSha256);
    }

    [Fact]
    public async Task Late_corruption_cannot_hide_after_the_first_two_thousand_records()
    {
        string path = Write("invalid.jsonl",
            Enumerable.Range(0, 2_000).Select(Chat)
                .Concat(new[] { "{missing-json" }));
        Assert.True(TrainingDatasetShape.Inspect(path).Truncated);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            QuickTrainingDatasetValidator.ValidateAsync(path, TrainingMethod.Sft));
        Assert.Contains("2001", error.Message);
    }

    [Fact]
    public async Task Mixed_late_method_records_fail_even_when_sample_matches()
    {
        string path = Write("mixed.jsonl",
            Enumerable.Range(0, 2_000).Select(Preference).Concat(new[] { Chat(2001) }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            QuickTrainingDatasetValidator.ValidateAsync(path, TrainingMethod.Dpo));
        Assert.Contains("2001", error.Message);
    }

    [Fact]
    public async Task Identical_chosen_and_rejected_answers_fail_closed()
    {
        string path = Write("same.jsonl",
            new[] { "{\"prompt\":\"q\",\"chosen\":\"same\",\"rejected\":\"same\"}" });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            QuickTrainingDatasetValidator.ValidateAsync(path, TrainingMethod.Dpo));
        Assert.Contains("identical", error.Message);
    }

    [Fact]
    public async Task Review_hash_refuses_modified_file_before_job_admission()
    {
        string path = Write("changed.jsonl", Enumerable.Range(0, 40).Select(Chat));
        var verified = await QuickTrainingDatasetValidator.ValidateAsync(
            path, TrainingMethod.Sft);
        File.AppendAllText(path, Chat(41) + Environment.NewLine);
        Assert.Throws<InvalidDataException>(() =>
            TrainingLabService.VerifyExpectedDatasetHash(verified.Sha256,
                TrainingJobDurability.ComputeFileSha256(path)));
        Assert.Throws<InvalidDataException>(() =>
            TrainingLabService.VerifyExpectedDatasetHash("bad", verified.Sha256));
        TrainingLabService.VerifyExpectedDatasetHash(null, verified.Sha256);
    }

    [Fact]
    public void Unverified_full_file_never_pretends_to_be_approved()
    {
        Assert.NotNull(QuickTrainingPolicy.RefusalAfterFullVerification(
            true, false, 2_001, new string('a', 64)));
        Assert.NotNull(QuickTrainingPolicy.RefusalAfterFullVerification(
            true, true, 2_001, "not-a-sha"));
        Assert.NotNull(QuickTrainingPolicy.RefusalAfterFullVerification(
            false, true, 2_001, new string('a', 64)));
    }

    [Fact]
    public async Task Abort_before_validation_is_observed_without_a_partial_approval()
    {
        string path = Write("cancel.jsonl", Enumerable.Range(0, 2500).Select(Chat));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            QuickTrainingDatasetValidator.ValidateAsync(path, TrainingMethod.Sft, cts.Token));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
