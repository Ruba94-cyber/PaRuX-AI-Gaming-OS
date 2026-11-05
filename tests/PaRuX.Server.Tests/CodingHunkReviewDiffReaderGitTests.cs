using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Real local Git fixture: no network, no AI model and no user repository.</summary>
public sealed class CodingHunkReviewDiffReaderGitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "parux-hunk-diff-git-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "original");
    private string Worktree => Path.Combine(_root, "task");

    public CodingHunkReviewDiffReaderGitTests()
    {
        Directory.CreateDirectory(Source);
        RunGit(Source, "init");
        RunGit(Source, "config", "user.name", "PaRuX Tests");
        RunGit(Source, "config", "user.email", "tests@parux.invalid");
        RunGit(Source, "config", "core.autocrlf", "false");
        string[] lines = Enumerable.Range(1, 24).Select(i => "line " + i).ToArray();
        File.WriteAllText(Path.Combine(Source, "sample.txt"), string.Join("\n", lines) + "\n");
        RunGit(Source, "add", "--", "sample.txt");
        RunGit(Source, "commit", "-m", "baseline");
        RunGit(Source, "worktree", "add", "-b", "parux-hunk-task", Worktree, "HEAD");
    }

    [Fact]
    public async Task Reads_real_tracked_diff_and_applies_only_approved_hunk()
    {
        string[] edited = File.ReadAllLines(Path.Combine(Worktree, "sample.txt"));
        edited[1] = "approved line 2";
        edited[20] = "rejected line 21";
        File.WriteAllText(Path.Combine(Worktree, "sample.txt"),
            string.Join("\n", edited) + "\n");

        var files = await CodingHunkReviewDiffReader.ChangedPathsAsync(
            Worktree, CancellationToken.None);
        Assert.NotNull(files);
        Assert.Equal("sample.txt", Assert.Single(files!));
        CodingHunkDiffRead read = await CodingHunkReviewDiffReader.ReadAsync(
            Worktree, "sample.txt", CancellationToken.None);
        Assert.True(read.Ready, read.Explanation);
        Assert.Equal(2, read.Hunks.Count);

        CodingReviewChoice[] choices = read.Hunks.Select((h, i) =>
            new CodingReviewChoice(h.Id, i == 0
                ? CodingHunkDecision.Accept : CodingHunkDecision.Reject)).ToArray();
        CodingHunkPreviewResult preview = CodingSelectiveHunkApplyService.PreparePreview(
            Source, "sample.txt", read.Diff!, choices);
        Assert.True(preview.Ready, preview.Explanation);

        CodingSelectiveHunkApplyResult result = CodingSelectiveHunkApplyService.Apply(
            Source, "sample.txt", read.Diff!, choices,
            preview.PreimageSha256, preview.CandidateSha256!, true);
        Assert.True(result.Applied, result.Explanation);
        string[] after = File.ReadAllLines(Path.Combine(Source, "sample.txt"));
        Assert.Equal("approved line 2", after[1]);
        Assert.Equal("line 21", after[20]);
        Assert.Equal("rejected line 21", File.ReadAllLines(
            Path.Combine(Worktree, "sample.txt"))[20]);
        Assert.Equal("baseline", RunGit(Source, "log", "-1", "--pretty=%s").Trim());
    }

    private static string RunGit(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Git executable could not be started.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000) || process.ExitCode != 0)
            throw new InvalidOperationException("Synthetic Git fixture failed: "
                + string.Join(" ", arguments) + " " + stderr);
        return stdout;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Source) && Directory.Exists(Worktree))
                RunGit(Source, "worktree", "remove", "--force", Worktree);
        }
        finally
        {
            if (Directory.Exists(_root))
            {
                // Git object files are read-only on Windows; clear before delete.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
