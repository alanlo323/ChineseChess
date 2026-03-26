using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;

namespace ChineseChess.Infrastructure.Tablebase;

/// <summary>
/// 殘局庫結論的記憶體存儲。
/// 以 Zobrist hash (ulong) 為鍵，<see cref="TablebaseEntry"/> 為值。
///
/// 執行緒安全說明：
///   Store/Clear 在單一背景執行緒（RetrogradAnalyzer）中呼叫；
///   WinCount / LossCount / DrawCount 使用 Interlocked 計數器確保 UI 執行緒
///   讀取時不發生 data race，且讀取為 O(1)。
///   Dictionary 本身不是執行緒安全；呼叫端負責確保寫入單執行緒化。
/// </summary>
public sealed class TablebaseStorage
{
    private readonly Dictionary<ulong, TablebaseEntry> entries = [];

    private int winCount;
    private int lossCount;
    private int drawCount;
    private int totalCount;

    public int TotalPositions => Volatile.Read(ref totalCount);
    public int WinCount  => Volatile.Read(ref winCount);
    public int LossCount => Volatile.Read(ref lossCount);
    public int DrawCount => Volatile.Read(ref drawCount);

    public void Store(ulong hash, TablebaseEntry entry)
    {
        entries[hash] = entry;
        Interlocked.Increment(ref totalCount);

        switch (entry.Result)
        {
            case TablebaseResult.Win:  Interlocked.Increment(ref winCount);  break;
            case TablebaseResult.Loss: Interlocked.Increment(ref lossCount); break;
            case TablebaseResult.Draw: Interlocked.Increment(ref drawCount); break;
        }
    }

    public TablebaseEntry Query(ulong hash) =>
        entries.TryGetValue(hash, out var e) ? e : TablebaseEntry.Unknown;

    public bool Contains(ulong hash) => entries.ContainsKey(hash);

    /// <summary>
    /// 回傳所有條目的快照列舉。
    /// 注意：迭代期間勿並行呼叫 Store/Clear，否則 Dictionary 會拋出修改例外。
    /// </summary>
    public IEnumerable<KeyValuePair<ulong, TablebaseEntry>> GetAllEntries() =>
        entries.ToList(); // 防禦性複製，避免迭代中途被修改

    public void Clear()
    {
        entries.Clear();
        Interlocked.Exchange(ref totalCount, 0);
        Interlocked.Exchange(ref winCount,   0);
        Interlocked.Exchange(ref lossCount,  0);
        Interlocked.Exchange(ref drawCount,  0);
    }
}
