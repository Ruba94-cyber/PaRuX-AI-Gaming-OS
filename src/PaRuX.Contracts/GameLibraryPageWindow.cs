namespace PaRuX.Contracts;

/// <summary>
/// First-party, bounded view-window computation for the local game library.
/// Never changes persistence, queries a storefront or starts background work.
/// </summary>
public readonly record struct GameLibraryPageWindow(
    int Page, int Offset, int Count, int Total)
{
    public int FirstVisible => Count == 0 ? 0 : Offset + 1;
    public int LastVisible => Offset + Count;
    public bool HasPrevious => Page > 0;
    public bool HasNext => LastVisible < Total;

    public static GameLibraryPageWindow For(int total, int requestedPage, int pageSize = 50)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        int lastPage = total == 0 ? 0 : (total - 1) / pageSize;
        int page = Math.Clamp(requestedPage, 0, lastPage);
        int offset = page * pageSize;
        return new(page, offset, Math.Min(pageSize, total - offset), total);
    }
}
