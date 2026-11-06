using System.Reflection;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// KTO requires genuine unpaired binary preference feedback and a real physical micro-batch.
/// Native GPU compatibility, throughput and checkpoint tests remain Windows acceptance gates.
/// </summary>
public sealed class KtoTrainingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-kto-" + Guid.NewGuid().ToString("N"));

    private static string Record(int index, bool label) =>
        JsonSerializer.Serialize(new { prompt = "Question " + index, completion = "Answer " + index, label });

    private string Save(string file, IEnumerable<string> records)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, file);
        File.WriteAllLines(path, records);
        return path;
    }

    [Fact]
    public void Unpaired_binary_feedback_with_both_classes_is_detected_and_plannable()
    {
        string path = Save("balanced.jsonl", Enumerable.Range(0, 100).Select(i => Record(i, i % 2 == 0)));
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(path);
        Assert.True(shape.SupportsKto);
        Assert.Equal(100, shape.ValidKtoRecords);
        Assert.Equal(50, shape.DesirableKtoRecords);
        Assert.Equal(50, shape.UndesirableKtoRecords);
        Assert.False(shape.SupportsPreferenceTuning);
        TrainingLabService.ValidateDatasetForMethod(path, TrainingMethod.Kto, null);

        TrainingRunPlanner.RunPlan plan = TrainingRunPlanner.Plan(
            shape, TrainingMethod.Kto, "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            16, 80, 5e-6, 16);
        Assert.True(plan.CanStart);
        Assert.Equal(TrainingMethod.Kto, plan.DetectedMethod);
    }

    [Theory]
    [InlineData("{\"prompt\":\"q\",\"completion\":\"a\",\"label\":\"true\"}")]
    [InlineData("{\"prompt\":\"q\",\"completion\":\"a\",\"label\":1}")]
    [InlineData("{\"prompt\":\"q\",\"completion\":\"\",\"label\":true}")]
    [InlineData("{\"prompt\":\"q\",\"label\":false}")]
    public void Invalid_label_or_missing_completion_cannot_be_used_as_KTO_feedback(string bad)
    {
        string path = Save("invalid.jsonl", new[] { bad });
        Assert.False(TrainingDatasetShape.Inspect(path).SupportsKto);
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateDatasetForMethod(
            path, TrainingMethod.Kto, null));
    }

    [Fact]
    public void One_sided_labels_are_refused_even_if_all_rows_are_valid_JSON()
    {
        string path = Save("one-sided.jsonl", Enumerable.Range(0, 80).Select(i => Record(i, true)));
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(path);
        Assert.Equal(80, shape.ValidKtoRecords);
        Assert.False(shape.SupportsKto);
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateDatasetForMethod(
            path, TrainingMethod.Kto, null));
    }

    [Fact]
    public void Late_invalid_record_is_rejected_after_the_bounded_plan_preview()
    {
        string path = Save("late-invalid.jsonl",
            Enumerable.Range(0, 2_000).Select(i => Record(i, i % 2 == 0))
            .Concat(new[] { "{\"prompt\":\"late\",\"completion\":\"bad\",\"label\":\"true\"}" }));
        TrainingDatasetShape.Shape shape = TrainingDatasetShape.Inspect(path);
        Assert.True(shape.SupportsKto);
        Assert.True(shape.Truncated);
        var error = Assert.Throws<InvalidDataException>(() =>
            TrainingLabService.ValidateDatasetForMethod(path, TrainingMethod.Kto, null));
        Assert.Contains("2001", error.Message);
    }

    [Fact]
    public void KL_estimation_needs_two_physical_samples_not_accumulation_steps()
    {
        Assert.Throws<InvalidOperationException>(() => TrainingLabService.ValidateKtoMicroBatch(1));
        TrainingLabService.ValidateKtoMicroBatch(2);
        TrainingLabService.ValidateKtoMicroBatch(4);
    }

    [Fact]
    public void KTO_is_accelerated_only_and_uses_the_pinned_TRL_runner()
    {
        Assert.Contains(TrainingMethod.Kto,
            TrainingRuntimeCatalog.MethodsForProbe(new TrainingRuntimeProbe
            { ImportsValid = true, Backend = "cuda" }));
        Assert.DoesNotContain(TrainingMethod.Kto,
            TrainingRuntimeCatalog.MethodsForProbe(new TrainingRuntimeProbe
            { ImportsValid = true, Backend = "cpu" }));

        FieldInfo? runner = typeof(TrainingLabService).GetField(
            "TrainScriptSource", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(runner);
        string python = Assert.IsType<string>(runner!.GetRawConstantValue());
        Assert.Contains("from trl import KTOTrainer, KTOConfig", python);
        Assert.Contains("trainer = KTOTrainer(", python);
        Assert.Contains("loss_type=\"kto\"", python);
        Assert.Contains("if cfg.get(\"batch_size\", 1) < 2:", python);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
