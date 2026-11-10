using PaRuX.Contracts;
using System.Diagnostics;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CoreStartFailureTests
{
    [Fact]
    public void Immediate_retry_ignores_old_failure_and_each_attempt_cleans_only_its_own_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-start-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Guid.NewGuid().ToString("N");
            string second = Guid.NewGuid().ToString("N");
            CoreStartFailure.TryWrite(root, first, "first failure");
            Assert.Null(CoreStartFailure.TryRead(root, second));
            CoreStartFailure.TryWrite(root, second, "second failure");
            Assert.Equal("first failure", CoreStartFailure.TryRead(root, first));
            Assert.Equal("second failure", CoreStartFailure.TryRead(root, second));
            CoreStartFailure.TryDelete(root, first);
            Assert.Null(CoreStartFailure.TryRead(root, first));
            Assert.Equal("second failure", CoreStartFailure.TryRead(root, second));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Missing_or_invalid_attempt_never_reads_writes_or_deletes(string? attempt)
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-start-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CoreStartFailure.TryWrite(root, attempt, "error");
            Assert.Null(CoreStartFailure.TryRead(root, attempt));
            CoreStartFailure.TryDelete(root, attempt);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Diagnostic_read_is_bounded_and_missing_directory_is_best_effort()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-start-test-" + Guid.NewGuid().ToString("N"));
        string attempt = Guid.NewGuid().ToString("N");
        CoreStartFailure.TryWrite(root, attempt, "cannot write");
        Assert.Null(CoreStartFailure.TryRead(root, attempt));
        Directory.CreateDirectory(root);
        try
        {
            CoreStartFailure.TryWrite(root, attempt, new string('x', 500));
            Assert.Equal(400, CoreStartFailure.TryRead(root, attempt)!.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_junction_data_root_or_ancestor_is_never_used_for_diagnostics(bool linkedAncestor)
    {
        if (!OperatingSystem.IsWindows()) return;
        string sandbox = Path.Combine(Path.GetTempPath(), "parux-start-test-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(sandbox, "target");
        string junction = Path.Combine(sandbox, "junction");
        Directory.CreateDirectory(target);
        string actualData = linkedAncestor ? Path.Combine(target, "data") : target;
        Directory.CreateDirectory(actualData);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + junction + "\" \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());

            string dataRoot = linkedAncestor ? Path.Combine(junction, "data") : junction;
            string attempt = Guid.NewGuid().ToString("N");
            CoreStartFailure.TryWrite(dataRoot, attempt, "failure");
            Assert.Empty(Directory.GetFiles(actualData));

            // Neither a forged diagnostic nor cleanup should traverse the junction.
            CoreStartFailure.TryWrite(actualData, attempt, "existing");
            Assert.Null(CoreStartFailure.TryRead(dataRoot, attempt));
            CoreStartFailure.TryDelete(dataRoot, attempt);
            Assert.Equal("existing", CoreStartFailure.TryRead(actualData, attempt));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
