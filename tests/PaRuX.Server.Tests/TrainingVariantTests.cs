using System.Reflection;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Functional settings admission and source contracts for the existing Unsloth runner.
/// Native Python/CUDA integration is a separate Windows acceptance gate.
/// </summary>
public sealed class TrainingVariantTests
{
    private static TrainingJobRequest Request(string method = TrainingMethod.Sft) => new()
    {
        TrainJsonlPath = "synthetic.jsonl",
        Method = method,
        BaseModel = "Qwen/Qwen2.5-7B-Instruct"
    };

    private static TrainingDatasetShape.Shape ChatDataset =>
        TrainingDatasetShape.Inspect(Enumerable.Range(0, 100).Select(i =>
            JsonSerializer.Serialize(new
            {
                messages = new[]
                {
                    new { role = "user", content = "question" },
                    new { role = "assistant", content = "answer " + i }
                }
            })));

    [Theory]
    [InlineData(null, TrainingPrecision.Qlora4Bit)]
    [InlineData("LORA-16BIT", TrainingPrecision.Lora16Bit)]
    public void Precision_normalizes_only_supported_backend_modes(string? input, string expected) =>
        Assert.Equal(expected, TrainingPrecision.Normalize(input));

    [Theory]
    [InlineData("fp8")]
    [InlineData("full-finetune")]
    [InlineData("8-bit")]
    public void Unsupported_precision_is_never_silently_treated_as_4bit(string value) =>
        Assert.Throws<InvalidDataException>(() => TrainingPrecision.Normalize(value));

    [Fact]
    public void Lora16bit_budget_is_distinct_and_can_prevent_a_predictable_oom()
    {
        var four = TrainingRunPlanner.Plan(ChatDataset, TrainingMethod.Sft,
            "unsloth/Qwen2.5-7B-Instruct-bnb-4bit", 16, 60, 2e-4, 12, TrainingPrecision.Qlora4Bit);
        var sixteen = TrainingRunPlanner.Plan(ChatDataset, TrainingMethod.Sft,
            "unsloth/Qwen2.5-7B-Instruct-bnb-4bit", 16, 60, 2e-4, 12, TrainingPrecision.Lora16Bit);
        Assert.True(four.CanStart);
        Assert.False(sixteen.CanStart);
        Assert.True(sixteen.EstimatedGib > four.EstimatedGib);
        Assert.Contains(sixteen.Blocking, x => x.Field == "baseModel");
    }

    [Fact]
    public void Requests_round_trip_to_durable_json_without_losing_variant_choices()
    {
        TrainingJobRequest original = Request();
        original.Precision = TrainingPrecision.Lora16Bit;
        original.TargetPreset = TrainingTargetPreset.AllLinear;
        original.PackSequences = true;
        string json = JsonSerializer.Serialize(original);
        TrainingJobRequest restored = JsonSerializer.Deserialize<TrainingJobRequest>(json)!;
        Assert.Equal(original.Precision, restored.Precision);
        Assert.Equal(original.TargetPreset, restored.TargetPreset);
        Assert.True(restored.PackSequences);
    }

    [Fact]
    public void Valid_text_training_variants_are_accepted_at_job_admission()
    {
        TrainingJobRequest request = Request();
        request.Precision = TrainingPrecision.Lora16Bit;
        request.TargetPreset = TrainingTargetPreset.AllLinear;
        request.PackSequences = true;
        TrainingLabService.ValidateRequest(request, TrainingMethod.Sft);
    }

    [Theory]
    [InlineData("not-a-precision", TrainingTargetPreset.Attention, TrainingMethod.Sft)]
    [InlineData(TrainingPrecision.Qlora4Bit, "missing-target", TrainingMethod.Sft)]
    [InlineData(TrainingPrecision.Lora16Bit, TrainingTargetPreset.Attention, TrainingMethod.VisionSft)]
    [InlineData(TrainingPrecision.Lora16Bit, TrainingTargetPreset.Attention, TrainingMethod.TtsSft)]
    [InlineData(TrainingPrecision.Qlora4Bit, TrainingTargetPreset.AllLinear, TrainingMethod.VisionSft)]
    public void Invalid_or_unimplemented_variants_fail_closed(
        string precision, string target, string method)
    {
        TrainingJobRequest request = Request(method);
        request.Precision = precision;
        request.TargetPreset = target;
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateRequest(request, method));
    }

    [Fact]
    public void Sixteen_bit_loading_must_refuse_a_prequantized_model_identifier()
    {
        TrainingJobRequest request = Request();
        request.Precision = TrainingPrecision.Lora16Bit;
        request.BaseModel = "unsloth/Qwen2.5-7B-Instruct-bnb-4bit";
        Assert.Contains("unquantized", Assert.Throws<InvalidDataException>(
            () => TrainingLabService.ValidateRequest(request, TrainingMethod.Sft)).Message);
    }

    [Theory]
    [InlineData(TrainingMethod.Sft, TrainingPrecision.Qlora4Bit, "QLoRA")]
    [InlineData(TrainingMethod.Dpo, TrainingPrecision.Qlora4Bit, "QLoRA-DPO")]
    [InlineData(TrainingMethod.Orpo, TrainingPrecision.Qlora4Bit, "QLoRA-ORPO")]
    [InlineData(TrainingMethod.Sft, TrainingPrecision.Lora16Bit, "LoRA-16bit")]
    [InlineData(TrainingMethod.Orpo, TrainingPrecision.Lora16Bit, "LoRA-16bit-ORPO")]
    [InlineData(TrainingMethod.TtsSft, TrainingPrecision.Qlora4Bit, "LoRA-TTS-SFT")]
    public void Completed_adapter_registry_describes_real_base_precision_and_objective(
        string method, string precision, string expected)
    {
        TrainingJobRequest request = Request(method);
        request.Precision = precision;
        Assert.Equal(expected, AdapterManagerService.TrainingAdapterMethodLabel(request));
    }

    [Fact]
    public void Sequence_packing_cannot_silently_affect_preference_training()
    {
        TrainingJobRequest request = Request(TrainingMethod.Dpo);
        request.PackSequences = true;
        Assert.Throws<InvalidDataException>(() => TrainingLabService.ValidateRequest(request, TrainingMethod.Dpo));
    }

    [Fact]
    public void Embedded_native_runner_consumes_all_three_training_variant_settings()
    {
        var field = typeof(TrainingLabService).GetField("TrainScriptSource",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        string script = Assert.IsType<string>(field!.GetRawConstantValue());
        Assert.Contains("load_in_4bit=(precision == \"qlora-4bit\")", script);
        Assert.Contains("target_modules=targets", script);
        Assert.Contains("from trl import ORPOTrainer, ORPOConfig", script);
        Assert.Contains("trainer = ORPOTrainer(", script);
        Assert.Contains("cfg.get(\"pack_sequences\", False)", script);
        Assert.Contains("missing \" + \", \".join(missing)", script);
    }
}
