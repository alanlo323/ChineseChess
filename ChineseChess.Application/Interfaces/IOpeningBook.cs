using ChineseChess.Domain.Entities;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 開局庫查詢介面。依 Zobrist hash 查詢局面，命中時回傳加權隨機選手。
/// </summary>
public interface IOpeningBook
{
    /// <summary>開局庫是否已載入（至少含一個局面）。</summary>
    bool IsLoaded { get; }

    /// <summary>開局庫中局面數量。</summary>
    int EntryCount { get; }

    /// <summary>
    /// 查詢開局庫。找到對應局面時將 <paramref name="move"/> 設為候選走法之一並回傳 true；
    /// 找不到或局面無合法候選時回傳 false。
    /// </summary>
    bool TryProbe(ulong zobristKey, out Move move);

    /// <summary>判斷開局庫是否含有指定局面的記錄。</summary>
    bool ContainsPosition(ulong zobristKey);
}
