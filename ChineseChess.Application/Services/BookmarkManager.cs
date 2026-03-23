using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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

    /// <summary>書籤存檔失敗時發出通知，呼叫端可決定是否提示使用者。</summary>
    public event Action<Exception>? SaveFailed;

    public BookmarkManager(string? persistencePath = null)
    {
        this.persistencePath = persistencePath;
    }

    public void AddBookmark(string name, string fen)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("書籤名稱不可為空白。", nameof(name));
        if (string.IsNullOrWhiteSpace(fen))  throw new ArgumentException("FEN 字串不可為空白。", nameof(fen));

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

    public IReadOnlyList<string> GetBookmarkNames()
    {
        return bookmarks.Keys.ToList();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping  // 以原始中文字儲存，提升可讀性
    };

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
        catch (JsonException)
        {
            // 書籤 JSON 損毀時靜默忽略，從空書籤開始
        }
        catch (IOException)
        {
            // 讀取失敗（如權限問題）時靜默忽略，從空書籤開始
        }
    }

    private void SaveIfConfigured()
    {
        if (persistencePath is null) return;

        try
        {
            var dir = Path.GetDirectoryName(persistencePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(bookmarks, SerializerOptions);
            File.WriteAllText(persistencePath, json, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 通知呼叫端存檔失敗，讓 UI 層決定是否提示使用者
            SaveFailed?.Invoke(ex);
        }
    }
}
