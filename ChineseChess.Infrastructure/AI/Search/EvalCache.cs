namespace ChineseChess.Infrastructure.AI.Search;

/// <summary>
/// 評估快取（Eval Hash Table）。
/// 儲存 Quiescence Search 中 <c>evaluator.Evaluate(board)</c> 的結果，
/// 相同局面（含行棋方，已編碼於 ZobristKey）可直接取出，跳過整個評估計算。
///
/// 設計：
///   - 512K entries，key/value 分離儲存（兩個並行陣列），每個 entry 12 bytes（ulong + int），合計 ≈ 6MB
///   - Lockless overwrite，每個 SearchWorker 獨立一份以避免多執行緒 torn-read
///   - 以 key % Size 為 index（Size 為 2 的冪，使用 &amp; 取代 % 提升效能）
/// </summary>
internal sealed class EvalCache
{
    private const int Size = 1 << 19; // 512K entries
    private const int Mask = Size - 1;

    private readonly ulong[] keys   = new ulong[Size];
    private readonly int[]   scores = new int[Size];

    /// <summary>
    /// 嘗試查找 <paramref name="key"/> 對應的評估分數。
    /// 命中時回傳 true 並設定 <paramref name="score"/>；未命中回傳 false。
    /// </summary>
    internal bool TryGet(ulong key, out int score)
    {
        int idx = (int)(key & Mask);
        if (keys[idx] == key)
        {
            score = scores[idx];
            return true;
        }
        score = 0;
        return false;
    }

    /// <summary>
    /// 以無鎖覆寫方式儲存 <paramref name="key"/> 對應的評估分數。
    /// </summary>
    internal void Store(ulong key, int score)
    {
        int idx = (int)(key & Mask);
        keys[idx]   = key;
        scores[idx] = score;
    }
}
