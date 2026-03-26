using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;

namespace ChineseChess.Infrastructure.Tablebase;

/// <summary>
/// 殘局庫倒推分析引擎（Retrograde Analysis，DTM）。
///
/// 算法概述：
/// 1. 列舉子力組合的所有合法局面
/// 2. 標記終局：無合法著法 → Loss(0)（將死或困斃）
/// 3. BFS 倒推：
///    Loss(n) 前驅 → Win(n+1)
///    Win(n) 前驅：undone 計數歸零 → Loss(n+1)
/// 4. 剩餘 Unknown → Draw
/// </summary>
public sealed class RetrogradAnalyzer
{
    private readonly TablebaseStorage storage;
    private readonly IReadOnlyDictionary<PieceConfiguration, TablebaseStorage>? subTablebases;

    public RetrogradAnalyzer(
        TablebaseStorage storage,
        IReadOnlyDictionary<PieceConfiguration, TablebaseStorage>? subTablebases)
    {
        this.storage = storage;
        this.subTablebases = subTablebases;
    }

    /// <summary>便利多載：接受子力組合，自動列舉後執行分析。</summary>
    public void Analyze(
        PieceConfiguration config,
        IProgress<(string phase, long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        storage.Clear();
        progress?.Report(("列舉局面", 0, 1));
        var allBoards = PositionEnumerator.Enumerate(config)
            .ToDictionary(b => b.ZobristKey);
        progress?.Report(("列舉局面", allBoards.Count, allBoards.Count));
        Analyze(allBoards, progress, ct);
    }

    /// <summary>
    /// 對預先窮舉的局面集合執行完整倒推分析，結果寫入 storage。
    /// 呼叫端必須先清空 storage 再呼叫此方法。
    /// 傳入已建立的字典可避免重複窮舉（效能優化）。
    /// </summary>
    public void Analyze(
        IReadOnlyDictionary<ulong, Board> allBoards,
        IProgress<(string phase, long done, long total)>? progress = null,
        CancellationToken ct = default)
    {
        long total = allBoards.Count;
        if (total == 0) return;

        // ── Phase 2：建立前驅映射表 & undone 計數 ────────────────────
        progress?.Report(("建立著法圖", 0, total));

        // predecessors[Q.hash] = 所有可以走一步到達 Q 的局面 hash（非吃子著法）
        var predecessors = new Dictionary<ulong, List<ulong>>();
        // undone[P.hash] = 尚未確認為對手 Win 的後繼數量
        var undone = new Dictionary<ulong, int>();

        var bfsQueue = new Queue<ulong>();

        long done = 0;
        foreach (var (hash, board) in allBoards)
        {
            ct.ThrowIfCancellationRequested();

            // 無合法著法 → Loss(0)（將死或困斃）
            if (board.HasNoLegalMoves(board.Turn))
            {
                storage.Store(hash, new TablebaseEntry(TablebaseResult.Loss, 0));
                bfsQueue.Enqueue(hash);
                undone[hash] = 0;
                done++;
                continue;
            }

            int unresolved = 0;

            foreach (var move in board.GenerateLegalMoves())
            {
                var targetPiece = board.GetPiece(move.To);
                bool isCapture = !targetPiece.IsNone;

                if (isCapture)
                {
                    // 吃子著法：查詢子表
                    var subResult = QuerySubTablebase(board, move);
                    if (subResult.Result == TablebaseResult.Loss)
                    {
                        // 吃子後對手必負 → 本局面必勝
                        if (!storage.Contains(hash))
                        {
                            storage.Store(hash, new TablebaseEntry(TablebaseResult.Win, subResult.Depth + 1));
                            bfsQueue.Enqueue(hash);
                        }
                    }
                    else if (subResult.Result == TablebaseResult.Win)
                    {
                        // 吃子後對手必勝 → 此著法對我方不利，undone 不增加
                    }
                    else
                    {
                        // Draw / Unknown：此路可能是保底和棋，納入 unresolved
                        unresolved++;
                    }
                }
                else
                {
                    // 非吃子著法：需透過 BFS 動態解析
                    board.MakeMove(move);
                    ulong succHash = board.ZobristKey;
                    board.UndoMove();

                    if (allBoards.ContainsKey(succHash))
                    {
                        if (!predecessors.TryGetValue(succHash, out var preds))
                        {
                            preds = [];
                            predecessors[succHash] = preds;
                        }
                        preds.Add(hash);
                        unresolved++;
                    }
                }
            }

            if (!storage.Contains(hash))
                undone[hash] = unresolved;

            done++;
            if (done % 1000 == 0)
                progress?.Report(("建立著法圖", done, total));
        }

        progress?.Report(("建立著法圖", total, total));

        // ── Phase 3：BFS 倒推 ─────────────────────────────────────────
        progress?.Report(("倒推分析", 0, total));
        done = 0;

        while (bfsQueue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var hash = bfsQueue.Dequeue();
            var entry = storage.Query(hash);
            done++;

            if (!predecessors.TryGetValue(hash, out var predList)) continue;

            foreach (var predHash in predList)
            {
                if (storage.Contains(predHash)) continue;

                if (entry.Result == TablebaseResult.Loss)
                {
                    // 前驅可以走到此 Loss 局面 → 前驅必勝
                    storage.Store(predHash, new TablebaseEntry(TablebaseResult.Win, entry.Depth + 1));
                    bfsQueue.Enqueue(predHash);
                }
                else if (entry.Result == TablebaseResult.Win)
                {
                    // 前驅走到此 Win 局面（對前驅方不利）→ 減少 undone 計數
                    if (undone.TryGetValue(predHash, out int cnt))
                    {
                        cnt--;
                        undone[predHash] = cnt;
                        if (cnt <= 0)
                        {
                            // 所有著法均通向對手的 Win → 前驅必負
                            storage.Store(predHash, new TablebaseEntry(TablebaseResult.Loss, entry.Depth + 1));
                            bfsQueue.Enqueue(predHash);
                        }
                    }
                }
            }

            if (done % 1000 == 0)
                progress?.Report(("倒推分析", done, total));
        }

        // ── Phase 4：剩餘未解局面標記為和棋 ─────────────────────────
        foreach (var hash in allBoards.Keys)
        {
            if (!storage.Contains(hash))
                storage.Store(hash, TablebaseEntry.Draw);
        }

        progress?.Report(("完成", total, total));
    }

    // ── 私有輔助 ────────────────────────────────────────────────────────

    /// <summary>查詢吃子後局面的子表結論（子表不存在時視為 Draw）。</summary>
    private TablebaseEntry QuerySubTablebase(Board board, Move captureMove)
    {
        if (subTablebases is null) return TablebaseEntry.Draw;

        board.MakeMove(captureMove);
        ulong subHash = board.ZobristKey;
        board.UndoMove();

        foreach (var (_, subStorage) in subTablebases)
        {
            var entry = subStorage.Query(subHash);
            if (entry.IsResolved) return entry;
        }

        return TablebaseEntry.Draw;
    }
}
