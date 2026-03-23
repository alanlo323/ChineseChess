using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChineseChess.Application.Services;

/// <summary>
/// 書籤管理：儲存局面 FEN 快照供快速還原。
/// 若建構時提供 <paramref name="persistencePath"/>，每次新增/刪除後自動寫入 JSON 檔。
/// 不提供路徑時維持純記憶體行為（向後相容，不影響現有測試）。
/// </summary>
public class BookmarkManager
{
    private readonly Dictionary<string, string> bookmarks = new();
    private readonly string? persistencePath;

    public BookmarkManager(string? persistencePath = null)
    {
        this.persistencePath = persistencePath;
    }

    public void AddBookmark(string name, string fen)
    {
        bookmarks[name] = fen;
        SaveIfConfigured();
    }

    public string? GetBookmark(string name)
    {
        return bookmarks.TryGetValue(name, out var fen) ? fen : null;
    }

    public void DeleteBookmark(string name)
    {
        bookmarks.Remove(name);
        SaveIfConfigured();
    }

    public IEnumerable<string> GetBookmarkNames()
    {
        return bookmarks.Keys.ToList();
    }

    /// <summary>從 JSON 檔同步載入書籤（建構後立即呼叫，檔案不存在時安靜略過）。</summary>
    public void Load()
    {
        if (persistencePath is null || !File.Exists(persistencePath)) return;

        try
        {
            var json = File.ReadAllText(persistencePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (loaded is null) return;

            foreach (var kv in loaded)
                bookmarks[kv.Key] = kv.Value;
        }
        catch (Exception)
        {
            // 書籤檔案損毀時靜默忽略，從空書籤開始
        }
    }

    private void SaveIfConfigured()
    {
        if (persistencePath is null) return;

        // 同步寫入：書籤數量極小，不需要非同步開銷
        var dir = Path.GetDirectoryName(persistencePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(bookmarks, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(persistencePath, json, Encoding.UTF8);
    }
}
