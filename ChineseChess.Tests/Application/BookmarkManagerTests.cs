using ChineseChess.Application.Services;
using System;
using System.IO;
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

    [Fact]
    public void AddBookmark_EmptyName_Throws()
    {
        var manager = new BookmarkManager();
        Assert.Throws<ArgumentException>(() => manager.AddBookmark("", InitialFen));
        Assert.Throws<ArgumentException>(() => manager.AddBookmark("   ", InitialFen));
    }

    [Fact]
    public void AddBookmark_EmptyFen_Throws()
    {
        var manager = new BookmarkManager();
        Assert.Throws<ArgumentException>(() => manager.AddBookmark("開局", ""));
        Assert.Throws<ArgumentException>(() => manager.AddBookmark("開局", "  "));
    }

    // ─── 持久化路徑測試 ────────────────────────────────────────────────────────

    [Fact]
    public void Persist_AddBookmark_WritesToFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bm_test_{Guid.NewGuid()}.json");
        try
        {
            var manager = new BookmarkManager(path);
            manager.AddBookmark("開局", InitialFen);

            Assert.True(File.Exists(path), "書籤檔案應在 AddBookmark 後存在");
            var content = File.ReadAllText(path);
            Assert.Contains("開局", content);
            Assert.Contains(InitialFen, content);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Persist_Load_RestoresBookmarks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bm_test_{Guid.NewGuid()}.json");
        try
        {
            // 第一個實例寫入
            var writer = new BookmarkManager(path);
            writer.AddBookmark("A", InitialFen);
            writer.AddBookmark("B", KingsOnlyFen);

            // 第二個實例從檔案還原
            var reader = new BookmarkManager(path);
            reader.Load();

            Assert.Equal(InitialFen,  reader.GetBookmark("A"));
            Assert.Equal(KingsOnlyFen, reader.GetBookmark("B"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Persist_DeleteBookmark_UpdatesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bm_test_{Guid.NewGuid()}.json");
        try
        {
            var manager = new BookmarkManager(path);
            manager.AddBookmark("A", InitialFen);
            manager.AddBookmark("B", KingsOnlyFen);
            manager.DeleteBookmark("A");

            // 從新實例驗證刪除已持久化
            var reader = new BookmarkManager(path);
            reader.Load();

            Assert.Null(reader.GetBookmark("A"));
            Assert.Equal(KingsOnlyFen, reader.GetBookmark("B"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Persist_Load_CorruptFile_SilentlyIgnores()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bm_test_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json !!!!");

            var manager = new BookmarkManager(path);
            var ex = Record.Exception(() => manager.Load());

            Assert.Null(ex);
            Assert.Empty(manager.GetBookmarkNames());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Persist_Load_MissingFile_SilentlyIgnores()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bm_notexist_{Guid.NewGuid()}.json");
        var manager = new BookmarkManager(path);
        var ex = Record.Exception(() => manager.Load());

        Assert.Null(ex);
        Assert.Empty(manager.GetBookmarkNames());
    }

    [Fact]
    public void Persist_SaveFailed_EventFired_WhenFileReadOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bm_readonly_{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{}");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Exception? captured = null;

        try
        {
            var manager = new BookmarkManager(path);
            manager.SaveFailed += ex => captured = ex;
            manager.AddBookmark("開局", InitialFen); // 寫入唯讀檔案 → IOException/UnauthorizedAccessException

            Assert.NotNull(captured);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }
}
