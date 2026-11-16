using System;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class RuntimeRecoveryGuidanceTests
{
    [Theory]
    [InlineData("A game is currently active; local AI start is blocked.", RuntimeRecoveryGuidance.Reason.GamingActive)]
    [InlineData("Gaming Mode became active while the model was loading.", RuntimeRecoveryGuidance.Reason.GamingActive)]
    [InlineData("The process is in OFF mode.", RuntimeRecoveryGuidance.Reason.AiOff)]
    [InlineData("No sufficiently powerful GPU detected; this feature needs one.", RuntimeRecoveryGuidance.Reason.AcceleratorUnavailable)]
    [InlineData("File not found: C:\\models\\Qwen3-Q4.gguf", RuntimeRecoveryGuidance.Reason.MissingModel)]
    [InlineData("File not found: C:\\runtime\\llama-server.exe", RuntimeRecoveryGuidance.Reason.MissingRuntime)]
    [InlineData("The bundled CPU runtime (llama.cpp-*-cpu) was not found.", RuntimeRecoveryGuidance.Reason.MissingRuntime)]
    [InlineData("cudaMalloc failed: out of memory", RuntimeRecoveryGuidance.Reason.OutOfMemory)]
    [InlineData("No installed Corporate CPU model fits the memory available under the current PaRuX profile.", RuntimeRecoveryGuidance.Reason.OutOfMemory)]
    [InlineData("Corporate CPU memory availability could not be measured; model load was refused.", RuntimeRecoveryGuidance.Reason.MemoryUnavailable)]
    [InlineData("Corporate CPU memory availability could not be measured safely, or the model footprint is unknown.", RuntimeRecoveryGuidance.Reason.MemoryUnavailable)]
    [InlineData("Not enough free memory for CPU mode: this model needs about 2.9 GB but only 2.1 GB is available within the memory limit.", RuntimeRecoveryGuidance.Reason.OutOfMemory)]
    [InlineData("llama-server.exe exited unexpectedly (exit code 0x00000001).", RuntimeRecoveryGuidance.Reason.RuntimeCrashed)]
    [InlineData("unknown model architecture 'qwen99'", RuntimeRecoveryGuidance.Reason.IncompatibleModel)]
    [InlineData("listen tcp: bind: address already in use", RuntimeRecoveryGuidance.Reason.PortOccupied)]
    [InlineData("Connection refused by local Core", RuntimeRecoveryGuidance.Reason.CoreUnavailable)]
    [InlineData("The PaRuX local model did not become ready within six minutes: timed out.", RuntimeRecoveryGuidance.Reason.TimedOut)]
    public void Core_error_patterns_have_actionable_recovery_categories(
        string raw, RuntimeRecoveryGuidance.Reason reason)
    {
        Assert.Equal(reason, RuntimeRecoveryGuidance.Classify(raw));
    }

    [Fact]
    public void Corporate_memory_503_body_is_localized_instead_of_exposing_raw_json()
    {
        const string body = """{"error":{"message":"Not enough free memory for CPU mode: model needs 2.9 GB; only 2.1 GB is available within the memory limit."}}""";
        Assert.Equal(RuntimeRecoveryGuidance.Reason.OutOfMemory, RuntimeRecoveryGuidance.Classify(body));
        string italian = RuntimeRecoveryGuidance.Describe(body, italian: true);
        Assert.Contains("memoria", italian, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Runtime Manager", italian);
        Assert.DoesNotContain("{", italian);
    }

    [Fact]
    public void Corporate_llama_crash_body_is_actionable_without_leaking_stderr_or_paths()
    {
        const string body = """{"error":{"message":"llama-server.exe exited unexpectedly. C:\\Users\\private\\runtime\\llama-server.exe: srv init\n"}}""";
        Assert.Equal(RuntimeRecoveryGuidance.Reason.RuntimeCrashed, RuntimeRecoveryGuidance.Classify(body));
        string italian = RuntimeRecoveryGuidance.Describe(body, italian: true);
        Assert.Contains("codice di uscita", italian);
        Assert.DoesNotContain("private", italian);
        Assert.DoesNotContain("srv init", italian);
    }

    [Fact]
    public void Corporate_unknown_memory_is_not_misreported_as_confirmed_out_of_memory()
    {
        const string raw = "Corporate CPU memory availability could not be measured; model load was refused.";
        Assert.Equal(RuntimeRecoveryGuidance.Reason.MemoryUnavailable,
            RuntimeRecoveryGuidance.Classify(raw));
        string italian = RuntimeRecoveryGuidance.Describe(raw, italian: true);
        Assert.Contains("misurare", italian);
        Assert.Contains("Runtime Manager", italian);
        Assert.DoesNotContain("insufficiente", italian, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_binding_failures_are_not_misdiagnosed_as_port_conflicts()
    {
        const string raw = "Binding model metadata failed because the profile did not match.";
        Assert.Equal(RuntimeRecoveryGuidance.Reason.Unknown, RuntimeRecoveryGuidance.Classify(raw));
        Assert.Equal(raw, RuntimeRecoveryGuidance.Describe(raw, italian: false));
    }

    [Fact]
    public void Missing_model_and_runtime_messages_do_not_expose_local_paths()
    {
        const string model = @"File not found: C:\Users\private\models\private-model.gguf";
        const string runtime = @"File not found: C:\Users\private\runtime\llama-server.exe";

        Assert.Contains("Model Hub", RuntimeRecoveryGuidance.Describe(model, italian: false));
        Assert.Contains("Runtime Manager", RuntimeRecoveryGuidance.Describe(runtime, italian: false));
        Assert.DoesNotContain("private", RuntimeRecoveryGuidance.Describe(model, italian: false));
        Assert.DoesNotContain("private", RuntimeRecoveryGuidance.Describe(runtime, italian: true));
    }

    [Fact]
    public void Resource_pressure_is_advisory_and_never_promises_unsafe_gpu_operations()
    {
        var en = RuntimeRecoveryGuidance.Describe("CUDA_ERROR_OUT_OF_MEMORY", italian: false);
        var it = RuntimeRecoveryGuidance.Describe("CUDA_ERROR_OUT_OF_MEMORY", italian: true);
        Assert.Contains("RAM/VRAM", en);
        Assert.Contains("smaller model", en);
        Assert.Contains("nessuna impostazione verrà cambiata automaticamente", it);
        Assert.DoesNotContain("kill", en, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gaming_errors_preempt_other_hardware_hints_and_preserve_hard_off()
    {
        const string mixed = "A game is currently active; CUDA out of memory.";
        Assert.Equal(RuntimeRecoveryGuidance.Reason.GamingActive,
            RuntimeRecoveryGuidance.Classify(mixed));
        string it = RuntimeRecoveryGuidance.Describe(mixed, italian: true);
        Assert.Contains("Gaming attivo", it);
        Assert.DoesNotContain("Modello", it, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeout_recovery_avoids_duplicate_model_starts()
    {
        string message = RuntimeRecoveryGuidance.Describe("Timed out", italian: false);
        Assert.Contains("check Runtime Manager status", message);
        Assert.Contains("avoid duplicate starts", message);
    }

    [Fact]
    public void Empty_error_has_localized_fallback_without_fake_success()
    {
        Assert.Equal(RuntimeRecoveryGuidance.Reason.Unknown,
            RuntimeRecoveryGuidance.Classify(null));
        Assert.Contains("controller", RuntimeRecoveryGuidance.Describe(null, false));
        Assert.Contains("controller", RuntimeRecoveryGuidance.Describe(null, true));
        Assert.Equal("Unclassified failure",
            RuntimeRecoveryGuidance.Describe("Unclassified failure", true));
    }
}
