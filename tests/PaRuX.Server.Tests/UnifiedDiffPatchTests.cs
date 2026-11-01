using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class UnifiedDiffPatchTests
{
    private static PatchApplyResult Run(string diff, params (string Path, string Content)[] files)
    {
        PatchParseResult parsed = UnifiedDiffPatch.Parse(diff);
        Assert.Null(parsed.Error);
        var map = files.ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);
        return UnifiedDiffPatch.Apply(parsed.Files, p => map.TryGetValue(p, out string? c) ? c : null);
    }

    private const string Hello = "line1\nline2\nline3\nline4\nline5\n";

    [Fact]
    public void Modifies_a_file_with_context()
    {
        var result = Run("--- a/f.txt\n+++ b/f.txt\n@@ -2,3 +2,3 @@\n line2\n-line3\n+LINE3\n line4\n", ("f.txt", Hello));
        Assert.True(result.Success);
        Assert.Equal("line1\nline2\nLINE3\nline4\nline5\n", result.Files[0].NewContent);
    }

    [Fact]
    public void Finds_the_hunk_when_line_numbers_drifted()
    {
        string moved = "new top\nanother\n" + Hello;
        var result = Run("--- a/f.txt\n+++ b/f.txt\n@@ -2,3 +2,3 @@\n line2\n-line3\n+X\n line4\n", ("f.txt", moved));
        Assert.True(result.Success);
        Assert.Equal("new top\nanother\nline1\nline2\nX\nline4\nline5\n", result.Files[0].NewContent);
    }

    [Fact]
    public void Applies_several_hunks_with_cumulative_offsets()
    {
        string diff = "--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,4 @@\n line1\n+ins a\n+ins b\n line2\n@@ -4,2 +6,2 @@\n line4\n-line5\n+LINE5\n";
        var result = Run(diff, ("f.txt", Hello));
        Assert.True(result.Success);
        Assert.Equal("line1\nins a\nins b\nline2\nline3\nline4\nLINE5\n", result.Files[0].NewContent);
    }

    [Fact]
    public void Pure_insertion_hunk_inserts_after_the_given_line()
    {
        var result = Run("--- a/f.txt\n+++ b/f.txt\n@@ -2,0 +3,1 @@\n+between\n", ("f.txt", Hello));
        Assert.True(result.Success);
        Assert.Equal("line1\nline2\nbetween\nline3\nline4\nline5\n", result.Files[0].NewContent);
    }

    [Fact]
    public void Preserves_crlf_line_endings()
    {
        var result = Run("--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,2 @@\n-a\n+A\n b\n", ("f.txt", "a\r\nb\r\nc\r\n"));
        Assert.True(result.Success);
        Assert.Equal("A\r\nb\r\nc\r\n", result.Files[0].NewContent);
    }

    [Fact]
    public void Accepts_crlf_patch_text_and_blank_context_lines_without_the_leading_space()
    {
        var result = Run("--- a/f.txt\r\n+++ b/f.txt\r\n@@ -1,3 +1,3 @@\r\n a\r\n\r\n-c\r\n+C\r\n", ("f.txt", "a\n\nc\n"));
        Assert.True(result.Success);
        Assert.Equal("a\n\nC\n", result.Files[0].NewContent);
    }

    [Fact]
    public void Trailing_whitespace_differences_are_tolerated_only_as_a_fallback()
    {
        var result = Run("--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,2 @@\n a\n-b\n+B\n", ("f.txt", "a   \nb\n"));
        Assert.True(result.Success);
        Assert.Equal("a   \nB\n", result.Files[0].NewContent);
    }

    [Fact]
    public void No_newline_marker_controls_the_final_newline()
    {
        var result = Run("--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+B\n\\ No newline at end of file\n", ("f.txt", "a\nb"));
        Assert.True(result.Success);
        Assert.Equal("a\nB", result.Files[0].NewContent);
    }

    [Fact]
    public void Creates_a_new_file_and_refuses_to_overwrite_an_existing_one()
    {
        string diff = "--- /dev/null\n+++ b/dir/new.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n";
        var created = Run(diff);
        Assert.True(created.Success);
        Assert.Equal("one\ntwo\n", created.Files[0].NewContent);
        Assert.False(Run(diff, ("dir/new.txt", "x\n")).Success);
    }

    [Fact]
    public void Deletes_a_file_only_when_its_content_matches()
    {
        string diff = "--- a/f.txt\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-a\n-b\n";
        var ok = Run(diff, ("f.txt", "a\nb\n"));
        Assert.True(ok.Success);
        Assert.Null(ok.Files[0].NewContent);
        Assert.False(Run(diff, ("f.txt", "a\nb\nextra\n")).Success);
        Assert.False(Run(diff).Success);
    }

    [Fact]
    public void A_mismatched_hunk_fails_everything_and_names_the_file()
    {
        string diff = "--- a/ok.txt\n+++ b/ok.txt\n@@ -1,1 +1,1 @@\n-a\n+A\n--- a/bad.txt\n+++ b/bad.txt\n@@ -1,1 +1,1 @@\n-nope\n+x\n";
        var result = Run(diff, ("ok.txt", "a\n"), ("bad.txt", "something else\n"));
        Assert.False(result.Success);
        Assert.Contains("bad.txt", result.Error);
        Assert.Contains("Nothing was applied", result.Error);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("dir\\file.txt")]
    [InlineData("con ")]
    [InlineData("dir/./x.txt")]
    public void Unsafe_paths_are_rejected_by_the_parser(string path)
    {
        string diff = $"--- a/{path}\n+++ b/{path}\n@@ -1,1 +1,1 @@\n-a\n+b\n";
        Assert.NotNull(UnifiedDiffPatch.Parse(diff).Error);
        Assert.False(UnifiedDiffPatch.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("rename from a.txt\nrename to b.txt\n--- a/a.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-x\n+y\n")]
    [InlineData("--- a/a.txt\n+++ b/b.txt\n@@ -1 +1 @@\n-x\n+y\n")]
    [InlineData("old mode 100644\nnew mode 100755\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-x\n+y\n")]
    [InlineData("GIT binary patch\nliteral 3\n")]
    [InlineData("Binary files a/x.png and b/x.png differ\n")]
    [InlineData("")]
    [InlineData("just some prose, not a patch")]
    [InlineData("--- a/a.txt\n+++ b/a.txt\n")]
    [InlineData("--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n-x\n")]
    [InlineData("--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n*x\n")]
    public void Unsupported_or_malformed_patches_are_rejected_not_guessed(string diff) =>
        Assert.NotNull(UnifiedDiffPatch.Parse(diff).Error);

    [Fact]
    public void Limits_protect_the_parser()
    {
        Assert.NotNull(UnifiedDiffPatch.Parse(new string('x', UnifiedDiffPatch.MaxDiffChars + 1)).Error);
        var many = string.Concat(Enumerable.Range(0, UnifiedDiffPatch.MaxFiles + 1)
            .Select(i => $"--- a/f{i}.txt\n+++ b/f{i}.txt\n@@ -1 +1 @@\n-a\n+b\n"));
        Assert.NotNull(UnifiedDiffPatch.Parse(many).Error);
    }

    [Fact]
    public void The_same_file_twice_in_one_patch_is_rejected()
    {
        string one = "--- a/f.txt\n+++ b/f.txt\n@@ -1 +1 @@\n-a\n+b\n";
        Assert.NotNull(UnifiedDiffPatch.Parse(one + one).Error);
    }

    [Fact]
    public void Git_style_header_noise_and_tab_timestamps_are_ignored()
    {
        string diff = "diff --git a/f.txt b/f.txt\nindex 111..222 100644\n--- a/f.txt\t2026-01-01\n+++ b/f.txt\t2026-01-02\n@@ -1,1 +1,1 @@\n-line1\n+L1\n";
        var result = Run(diff, ("f.txt", Hello));
        Assert.True(result.Success);
        Assert.StartsWith("L1\n", result.Files[0].NewContent);
    }
}
