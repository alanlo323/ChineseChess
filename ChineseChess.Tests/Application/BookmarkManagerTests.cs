using ChineseChess.Application.Services;
using System.Linq;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// BookmarkManager 書籤新增、讀取、刪除、覆寫的完整性測試。
/// </summary>
public class BookmarkManagerTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
    private const string KingsOnlyFen = "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1";

    [Fact]
    public void AddBookmark_ThenGet_ReturnsCorrectFen()
    {
        var manager = new BookmarkManager();
        manager.AddBookmark("開局", InitialFen);
        Assert.Equal(InitialFen, manager.GetBookmark("開局"));
    }

    [Fact]
    public void GetBookmark_NonExistent_ReturnsNull()
    {
        var manager = new BookmarkManager();
        Assert.Null(manager.GetBookmark("不存在"));
    }

    [Fact]
    public void AddBookmark_Overwrite_ReturnsNewFen()
    {
        var manager = new BookmarkManager();
        manager.AddBookmark("局面", InitialFen);
        manager.AddBookmark("局面", KingsOnlyFen);
        Assert.Equal(KingsOnlyFen, manager.GetBookmark("局面"));
    }

    [Fact]
    public void DeleteBookmark_ThenGet_ReturnsNull()
    {
        var manager = new BookmarkManager();
        manager.AddBookmark("開局", InitialFen);
        manager.DeleteBookmark("開局");
        Assert.Null(manager.GetBookmark("開局"));
    }

    [Fact]
    public void DeleteBookmark_NonExistent_DoesNotThrow()
    {
        var manager = new BookmarkManager();
        var ex = Record.Exception(() => manager.DeleteBookmark("不存在"));
        Assert.Null(ex);
    }

    [Fact]
    public void GetBookmarkNames_ReturnsAllAddedNames()
    {
        var manager = new BookmarkManager();
        manager.AddBookmark("A", InitialFen);
        manager.AddBookmark("B", KingsOnlyFen);
        manager.AddBookmark("C", InitialFen);

        var names = manager.GetBookmarkNames().ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("A", names);
        Assert.Contains("B", names);
        Assert.Contains("C", names);
    }

    [Fact]
    public void GetBookmarkNames_Initially_ReturnsEmpty()
    {
        var manager = new BookmarkManager();
        Assert.Empty(manager.GetBookmarkNames());
    }

    [Fact]
    public void GetBookmarkNames_AfterDelete_ExcludesDeleted()
    {
        var manager = new BookmarkManager();
        manager.AddBookmark("A", InitialFen);
        manager.AddBookmark("B", KingsOnlyFen);
        manager.DeleteBookmark("A");

        var names = manager.GetBookmarkNames().ToList();
        Assert.Single(names);
        Assert.DoesNotContain("A", names);
        Assert.Contains("B", names);
    }

    [Fact]
    public void AddBookmark_Overwrite_DoesNotDuplicateName()
    {
        var manager = new BookmarkManager();
        manager.AddBookmark("局面", InitialFen);
        manager.AddBookmark("局面", KingsOnlyFen);

        var names = manager.GetBookmarkNames().ToList();
        Assert.Single(names);
    }

    [Fact]
    public void MultipleBookmarks_IndependentStorage()
    {
        var manager = new BookmarkManager();
        manager.AddBookmark("第一局", InitialFen);
        manager.AddBookmark("第二局", KingsOnlyFen);

        Assert.Equal(InitialFen, manager.GetBookmark("第一局"));
        Assert.Equal(KingsOnlyFen, manager.GetBookmark("第二局"));
    }
}
