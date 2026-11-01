using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GameLibraryPageWindowTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, false, false)]
    [InlineData(50, 0, 0, 50, 50, false, false)]
    [InlineData(51, 0, 0, 50, 51, false, true)]
    [InlineData(51, 1, 50, 1, 51, true, false)]
    [InlineData(151, 2, 100, 50, 151, true, true)]
    [InlineData(151, 3, 150, 1, 151, true, false)]
    [InlineData(151, 9999, 150, 1, 151, true, false)]
    [InlineData(151, -2, 0, 50, 151, false, true)]
    public void Bounds_never_skip_duplicate_or_show_outside_the_catalog(
        int total, int page, int offset, int count, int expectedTotal, bool previous, bool next)
    {
        GameLibraryPageWindow window = GameLibraryPageWindow.For(total, page);
        Assert.Equal(offset, window.Offset);
        Assert.Equal(count, window.Count);
        Assert.Equal(expectedTotal, window.Total);
        Assert.Equal(previous, window.HasPrevious);
        Assert.Equal(next, window.HasNext);
    }

    [Fact]
    public void Filtering_resets_page_and_last_page_is_clamped()
    {
        GameLibraryPageWindow newFilter = GameLibraryPageWindow.For(3, 9);
        Assert.Equal(0, newFilter.Page);
        Assert.Equal(1, newFilter.FirstVisible);
        Assert.Equal(3, newFilter.LastVisible);
    }

    [Fact]
    public void Invalid_page_sizes_and_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameLibraryPageWindow.For(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameLibraryPageWindow.For(10, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameLibraryPageWindow.For(10, 0, 201));
    }

    [Fact]
    public void Even_int_max_catalog_size_does_not_overflow_navigation_arithmetic()
    {
        GameLibraryPageWindow window = GameLibraryPageWindow.For(int.MaxValue, int.MaxValue);
        Assert.Equal(int.MaxValue, window.LastVisible);
        Assert.False(window.HasNext);
        Assert.InRange(window.Count, 1, 50);
    }
}
