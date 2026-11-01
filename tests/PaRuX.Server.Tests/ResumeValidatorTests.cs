using System.Net.Http.Headers;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ResumeValidatorTests
{
    private static string Partial() => Path.Combine(Path.GetTempPath(), "parux-resume-" + Guid.NewGuid().ToString("N") + ".gguf.partial");

    [Fact]
    public void Strong_etag_round_trips_and_delete_removes_it()
    {
        string partial = Partial();
        try
        {
            ResumeValidator.Write(partial, new EntityTagHeaderValue("\"abc123\""));
            Assert.Equal("\"abc123\"", ResumeValidator.Read(partial)?.Tag);
            ResumeValidator.Delete(partial);
            Assert.Null(ResumeValidator.Read(partial));
        }
        finally { ResumeValidator.Delete(partial); }
    }

    [Fact]
    public void Weak_or_missing_etag_is_never_used_for_if_range()
    {
        string partial = Partial();
        try
        {
            ResumeValidator.Write(partial, new EntityTagHeaderValue("\"abc\"", isWeak: true));
            Assert.Null(ResumeValidator.Read(partial));
            ResumeValidator.Write(partial, null);
            Assert.Null(ResumeValidator.Read(partial));
        }
        finally { ResumeValidator.Delete(partial); }
    }

    [Fact]
    public void Tampered_validator_file_is_ignored()
    {
        string partial = Partial();
        try
        {
            File.WriteAllText(partial + ".validator", new string('x', 4096));
            Assert.Null(ResumeValidator.Read(partial));
            File.WriteAllText(partial + ".validator", "W/\"weak\"");
            Assert.Null(ResumeValidator.Read(partial));
        }
        finally { ResumeValidator.Delete(partial); }
    }

    [Theory]
    [InlineData(100L, 100L, true)]
    [InlineData(0L, 100L, false)]
    [InlineData(101L, 100L, false)]
    public void Partial_content_must_start_exactly_where_local_bytes_end(long from, long local, bool expected) =>
        Assert.Equal(expected, ResumeValidator.ContentRangeMatches(new ContentRangeHeaderValue(from, from + 10, from + 1000), local));

    [Fact]
    public void Missing_content_range_is_rejected() =>
        Assert.False(ResumeValidator.ContentRangeMatches(null, 100));
}
