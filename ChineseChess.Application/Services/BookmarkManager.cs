using ChineseChess.Domain.Entities;
using System.Collections.Generic;
using System.Linq;

namespace ChineseChess.Application.Services;

public class BookmarkManager
{
    private readonly Dictionary<string, string> _bookmarks = new Dictionary<string, string>(); // Name -> FEN

    public void AddBookmark(string name, string fen)
    {
        _bookmarks[name] = fen;
    }

    public string? GetBookmark(string name)
    {
        return _bookmarks.TryGetValue(name, out var fen) ? fen : null;
    }

    public void DeleteBookmark(string name)
    {
        _bookmarks.Remove(name);
    }

    public IEnumerable<string> GetBookmarkNames()
    {
        return _bookmarks.Keys.ToList();
    }
}
