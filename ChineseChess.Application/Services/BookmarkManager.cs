using ChineseChess.Domain.Entities;
using System.Collections.Generic;
using System.Linq;

namespace ChineseChess.Application.Services;

public class BookmarkManager
{
    private readonly Dictionary<string, string> bookmarks = new Dictionary<string, string>(); // 書籤名稱 -> FEN

    public void AddBookmark(string name, string fen)
    {
        bookmarks[name] = fen;
    }

    public string? GetBookmark(string name)
    {
        return bookmarks.TryGetValue(name, out var fen) ? fen : null;
    }

    public void DeleteBookmark(string name)
    {
        bookmarks.Remove(name);
    }

    public IEnumerable<string> GetBookmarkNames()
    {
        return bookmarks.Keys.ToList();
    }
}
