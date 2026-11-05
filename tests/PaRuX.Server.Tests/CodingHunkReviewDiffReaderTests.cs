using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingHunkReviewDiffReaderTests
{
    [Fact]
    public void Git_name_only_zero_list_is_parsed_without_losing_last_path()
    {
        IReadOnlyList<string>? parsed = CodingHunkReviewDiffReader.ParsePathList(
            "src/Agent.cs\0tests/AgentTests.cs\0");
        Assert.Equal(new[] { "src/Agent.cs", "tests/AgentTests.cs" }, parsed);
        Assert.Empty(CodingHunkReviewDiffReader.ParsePathList("")!);
    }

    [Theory]
    [InlineData("../escape.cs")]
    [InlineData("src/../escape.cs")]
    [InlineData("/root/source.cs")]
    [InlineData("C:/source.cs")]
    [InlineData("src\\source.cs")]
    [InlineData(".git/config")]
    [InlineData("src/.git/config")]
    [InlineData("two words.cs")]
    [InlineData("tab\tpath.cs")]
    [InlineData("a//b.cs")]
    public void Untrusted_paths_are_rejected_before_git_execution(string path) =>
        Assert.False(CodingHunkReviewDiffReader.SafePath(path));

    [Fact]
    public void Malformed_truncated_oversized_and_duplicate_path_lists_fail_closed()
    {
        Assert.Null(CodingHunkReviewDiffReader.ParsePathList(null));
        Assert.Null(CodingHunkReviewDiffReader.ParsePathList("src/file.cs"));
        Assert.Null(CodingHunkReviewDiffReader.ParsePathList("src/file.cs\0src/file.cs\0"));
        Assert.Null(CodingHunkReviewDiffReader.ParsePathList(
            string.Join("", Enumerable.Range(0, 101).Select(i => "src/f" + i + ".cs\0"))));
        Assert.Null(CodingHunkReviewDiffReader.ParsePathList(new string('a', 65_537) + "\0"));
    }

    [Fact]
    public async Task Missing_worktree_cannot_be_mistaken_for_a_clean_verified_diff()
    {
        var listing = await CodingHunkReviewDiffReader.ChangedPathsAsync(
            Path.Combine(Path.GetTempPath(), "parux-missing-worktree-"
                + Guid.NewGuid().ToString("N")), CancellationToken.None);
        Assert.Null(listing);
        var attempt = await CodingHunkReviewDiffReader.ReadAsync(
            Path.Combine(Path.GetTempPath(), "parux-missing-worktree-"
                + Guid.NewGuid().ToString("N")), "src/test.cs", CancellationToken.None);
        Assert.False(attempt.Ready);
        Assert.Null(attempt.Diff);
    }
}
