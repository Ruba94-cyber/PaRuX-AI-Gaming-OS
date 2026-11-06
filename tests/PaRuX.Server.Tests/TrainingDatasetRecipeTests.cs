using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Intelligence;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Real local CSV/JSONL rows, no HF network, cloud models or synthetic target generation.</summary>
public sealed class TrainingDatasetRecipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-training-recipe-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DatasetCurationService _service;

    public TrainingDatasetRecipeTests()
    {
        Directory.CreateDirectory(_root);
        _service = new DatasetCurationService
        {
            TrainingRecipeTestOutputRoot = Path.Combine(_root, "managed")
        };
    }

    private string Source(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task Csv_sft_recipe_preserves_system_user_answer_and_original_file()
    {
        string input = Source("supervised.csv",
            "question,answer,system\n\"What is CAN?\",\"A vehicle bus, not a car.\",\"Be concise\"\n\"Which bus?\",\"Controller Area Network.\",\n");
        byte[] before = await File.ReadAllBytesAsync(input);
        TrainingDatasetRecipeResult recipe = await _service.CreateTrainingRecipeAsync(new()
        {
            SourcePath = input, Method = TrainingMethod.Sft,
            PromptColumn = "question", ResponseColumn = "answer", SystemColumn = "system"
        }, CancellationToken.None);

        Assert.Equal(2, recipe.WrittenRows);
        Assert.Equal(0, recipe.SkippedRows);
        Assert.Equal(before, await File.ReadAllBytesAsync(input));
        Assert.StartsWith(Path.Combine(_root, "managed"), recipe.TrainJsonlPath, StringComparison.OrdinalIgnoreCase);
        using JsonDocument first = JsonDocument.Parse(File.ReadLines(recipe.TrainJsonlPath).First());
        var turns = first.RootElement.GetProperty("messages");
        Assert.Equal("system", turns[0].GetProperty("role").GetString());
        Assert.Equal("user", turns[1].GetProperty("role").GetString());
        Assert.Equal("assistant", turns[2].GetProperty("role").GetString());
        Assert.Equal("A vehicle bus, not a car.", turns[2].GetProperty("content").GetString());
        Assert.True(TrainingDatasetShape.Inspect(recipe.TrainJsonlPath).SupportsSupervisedFineTuning);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(recipe.TrainJsonlPath))).ToLowerInvariant(), recipe.Sha256);
    }

    [Fact]
    public async Task Dpo_recipe_never_invents_rejected_answers()
    {
        string path = Source("preference.csv",
            "input,better,worse\nq1,yes,no\nq2,safe,unsafe\nq3,green,red\nq4,good,bad\n");
        TrainingDatasetRecipeResult result = await _service.CreateTrainingRecipeAsync(new()
        {
            SourcePath = path, Method = TrainingMethod.Dpo,
            PromptColumn = "input", ResponseColumn = "better", ExtraColumn = "worse"
        }, CancellationToken.None);
        Assert.Equal(4, result.WrittenRows);
        using JsonDocument first = JsonDocument.Parse(File.ReadLines(result.TrainJsonlPath).First());
        Assert.Equal("yes", first.RootElement.GetProperty("chosen").GetString());
        Assert.Equal("no", first.RootElement.GetProperty("rejected").GetString());
        Assert.True(TrainingDatasetShape.Inspect(result.TrainJsonlPath).SupportsPreferenceTuning);
    }

    [Fact]
    public async Task Kto_recipe_converts_only_real_boolean_labels_and_requires_both_classes()
    {
        string path = Source("binary.jsonl",
            "{\"p\":\"q1\",\"a\":\"yes\",\"feedback\":true}\n" +
            "{\"p\":\"q2\",\"a\":\"no\",\"feedback\":false}\n" +
            "{\"p\":\"q3\",\"a\":\"green\",\"feedback\":true}\n" +
            "{\"p\":\"q4\",\"a\":\"red\",\"feedback\":false}\n");
        TrainingDatasetRecipeResult result = await _service.CreateTrainingRecipeAsync(new()
        {
            SourcePath = path, Method = TrainingMethod.Kto,
            PromptColumn = "p", ResponseColumn = "a", ExtraColumn = "feedback"
        }, CancellationToken.None);
        var shape = TrainingDatasetShape.Inspect(result.TrainJsonlPath);
        Assert.True(shape.SupportsKto);
        Assert.Equal(2, shape.DesirableKtoRecords);
        Assert.Equal(2, shape.UndesirableKtoRecords);
    }

    [Fact]
    public async Task Invalid_kto_label_never_leaves_a_output_artifact()
    {
        string path = Source("bad-feedback.csv",
            "p,a,label\nq1,yes,1\nq2,no,false\nq3,yes,true\nq4,no,false\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateTrainingRecipeAsync(new()
        {
            SourcePath = path, Method = TrainingMethod.Kto,
            PromptColumn = "p", ResponseColumn = "a", ExtraColumn = "label"
        }, CancellationToken.None));
        Assert.Empty(Directory.Exists(Path.Combine(_root, "managed"))
            ? Directory.GetFiles(Path.Combine(_root, "managed")) : Array.Empty<string>());
    }

    [Fact]
    public async Task Missing_or_duplicate_column_mapping_refused_without_output()
    {
        string path = Source("map.csv", "prompt,response\nq,answer\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateTrainingRecipeAsync(new()
        {
            SourcePath = path, Method = TrainingMethod.Sft,
            PromptColumn = "prompt", ResponseColumn = "prompt"
        }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateTrainingRecipeAsync(new()
        {
            SourcePath = path, Method = TrainingMethod.Sft,
            PromptColumn = "prompt", ResponseColumn = "missing-column"
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Too_many_incomplete_rows_must_be_cleaned_before_export()
    {
        string path = Source("incomplete.csv",
            "prompt,response\nq1,answer1\nq2,\nq3,answer3\nq4,answer4\nq5,answer5\n");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateTrainingRecipeAsync(new()
        {
            SourcePath = path, Method = TrainingMethod.Sft,
            PromptColumn = "prompt", ResponseColumn = "response"
        }, CancellationToken.None));
        Assert.Contains("5%", error.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
