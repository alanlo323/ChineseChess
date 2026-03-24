using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Helpers;

namespace ChineseChess.Infrastructure.AI.Nnue.Features;

/// <summary>
/// HalfKAv2_hm 特徵計算：IBoard → 稀疏特徵索引陣列。
/// 每個局面有兩個視角（紅方/黑方），各自產生約 30 個活躍特徵。
/// </summary>
public static class HalfKAv2Features
{
    /// <summary>
    /// 計算指定視角的進攻 bucket（0-3）。
    /// 0 = 無車/馬/炮；1 = 有馬/炮但無車；2 = 有車但無馬/炮；3 = 車+馬/炮皆有。
    /// </summary>
    public static int ComputeAttackBucket(IBoard board, PieceColor perspective)
    {
        bool hasRook = false, hasKnightOrCannon = false;
        for (int i = 0; i < 90; i++)
        {
            var p = board.GetPiece(i);
            if (p.IsNone || p.Color != perspective) continue;
            if (p.Type == PieceType.Rook) hasRook = true;
            else if (p.Type is PieceType.Horse or PieceType.Cannon)
                hasKnightOrCannon = true;
        }
        return (hasRook ? 2 : 0) + (hasKnightOrCannon ? 1 : 0);
    }

    /// <summary>
    /// 填入指定視角的所有活躍特徵索引，回傳實際特徵數量。
    /// <paramref name="output"/> 長度至少需為 32（每面最多約 30 個非王棋子）。
    /// </summary>
    public static int GetActiveFeatures(
        IBoard board,
        PieceColor perspective,
        bool midMirror,
        int[] output)
    {
        // 取得己方王的位置
        int ownKingSq = FindKing(board, perspective);
        if (ownKingSq < 0) return 0;  // 王不存在（不應發生於合法局面）

        int enemyKingSq = FindKing(board, FlipColor(perspective));
        if (enemyKingSq < 0) return 0;

        var (kingBucket, mirror) = HalfKAv2Tables.GetKingBucket(ownKingSq, enemyKingSq, midMirror);
        int attackBucket = ComputeAttackBucket(board, perspective);
        int combinedBucket = kingBucket * HalfKAv2Tables.AttackBucketNb + attackBucket;
        int rankFlip = perspective == PieceColor.Black ? 1 : 0;

        int count = 0;
        for (int csIdx = 0; csIdx < 90; csIdx++)
        {
            var piece = board.GetPiece(csIdx);
            if (piece.IsNone || piece.Type == PieceType.King) continue;

            int allPiecesIdx = HalfKAv2Tables.ToAllPiecesIndex(piece.Color, piece.Type, perspective);
            if (allPiecesIdx < 0) continue;

            int transformedSq = HalfKAv2Tables.IndexMap[mirror ? 1 : 0, rankFlip, csIdx];
            short psqOffset = HalfKAv2Tables.PSQOffsets[allPiecesIdx, transformedSq];
            if (psqOffset < 0) continue;  // 不在 ValidBB 內（理論上不應發生於合法局面）

            output[count++] = psqOffset + HalfKAv2Tables.PsNb * combinedBucket;
        }
        return count;
    }

    /// <summary>
    /// 走棋增量更新時，計算需要移除和新增的特徵索引。
    /// 若 bucket 或鏡像狀態改變，回傳 NeedsRefresh = true，呼叫端應做全量刷新。
    /// </summary>
    /// <param name="boardAfterMove">走棋後的棋盤狀態</param>
    /// <param name="perspective">計算此視角的特徵</param>
    /// <param name="move">已執行的走法</param>
    /// <param name="movedPiece">被移動的棋子（走棋前的棋子）</param>
    /// <param name="capturedPiece">被吃的棋子（可為 Piece.None）</param>
    /// <param name="midMirrorBefore">走棋前的 mid-mirror 旗標</param>
    /// <param name="combinedBucketBefore">走棋前的 combinedBucket（用於偵測 bucket 變更）</param>
    /// <param name="mirrorBefore">走棋前的 mirror 旗標</param>
    public static ChangedFeatures GetChangedFeatures(
        IBoard boardAfterMove,
        PieceColor perspective,
        Move move,
        Piece movedPiece,
        Piece capturedPiece,
        bool midMirrorBefore,
        int combinedBucketBefore,
        bool mirrorBefore)
    {
        // 任一方王移動：king bucket 改變，需全量刷新
        if (movedPiece.Type == PieceType.King)
            return ChangedFeatures.RequiresRefresh;

        int ownKingSq = FindKing(boardAfterMove, perspective);
        int enemyKingSq = FindKing(boardAfterMove, FlipColor(perspective));
        if (ownKingSq < 0 || enemyKingSq < 0) return ChangedFeatures.RequiresRefresh;

        // 走棋後重新計算 bucket
        bool midMirrorAfter = MidMirrorEncoder.RequiresMidMirror(boardAfterMove, perspective);
        var (kingBucket, mirror) = HalfKAv2Tables.GetKingBucket(ownKingSq, enemyKingSq, midMirrorAfter);
        int attackBucketAfter = ComputeAttackBucket(boardAfterMove, perspective);
        int combinedBucketAfter = kingBucket * HalfKAv2Tables.AttackBucketNb + attackBucketAfter;

        // bucket 或鏡像發生變化：需全量刷新
        if (combinedBucketAfter != combinedBucketBefore || mirror != mirrorBefore)
            return ChangedFeatures.RequiresRefresh;

        int rankFlip = perspective == PieceColor.Black ? 1 : 0;
        int mirrorIdx = mirror ? 1 : 0;

        int removedCount = 0, addedCount = 0;
        var removed = new int[4];
        var added = new int[4];

        // 被移動的棋子：從 move.From 移除
        int fromIdx = HalfKAv2Tables.ToAllPiecesIndex(movedPiece.Color, movedPiece.Type, perspective);
        if (fromIdx >= 0)
        {
            int tFrom = HalfKAv2Tables.IndexMap[mirrorIdx, rankFlip, move.From];
            short psq = HalfKAv2Tables.PSQOffsets[fromIdx, tFrom];
            if (psq >= 0) removed[removedCount++] = psq + HalfKAv2Tables.PsNb * combinedBucketAfter;
        }

        // 被移動的棋子：在 move.To 新增
        if (fromIdx >= 0)
        {
            int tTo = HalfKAv2Tables.IndexMap[mirrorIdx, rankFlip, move.To];
            short psq = HalfKAv2Tables.PSQOffsets[fromIdx, tTo];
            if (psq >= 0) added[addedCount++] = psq + HalfKAv2Tables.PsNb * combinedBucketAfter;
        }

        // 被吃的棋子：從 move.To 移除
        if (!capturedPiece.IsNone)
        {
            int capIdx = HalfKAv2Tables.ToAllPiecesIndex(capturedPiece.Color, capturedPiece.Type, perspective);
            if (capIdx >= 0)
            {
                int tCap = HalfKAv2Tables.IndexMap[mirrorIdx, rankFlip, move.To];
                short psq = HalfKAv2Tables.PSQOffsets[capIdx, tCap];
                if (psq >= 0) removed[removedCount++] = psq + HalfKAv2Tables.PsNb * combinedBucketAfter;
            }
        }

        return new ChangedFeatures(removed[..removedCount], added[..addedCount], needsRefresh: false);
    }

    private static int FindKing(IBoard board, PieceColor color) =>
        NnueFeatureHelper.FindKing(board, color);

    private static PieceColor FlipColor(PieceColor color) =>
        color == PieceColor.Red ? PieceColor.Black : PieceColor.Red;
}

/// <summary>走棋增量更新的特徵差分結果。</summary>
public readonly struct ChangedFeatures
{
    public readonly int[] Removed;
    public readonly int[] Added;
    public readonly bool NeedsRefresh;

    public ChangedFeatures(int[] removed, int[] added, bool needsRefresh)
    {
        Removed = removed;
        Added = added;
        NeedsRefresh = needsRefresh;
    }

    public static ChangedFeatures RequiresRefresh { get; } =
        new ChangedFeatures([], [], needsRefresh: true);
}
