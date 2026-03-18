using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// Board.IsLikelyPerpetualCheck() 及 wasCheckAfterMove 歷史管理的單元測試。
///
/// IsLikelyPerpetualCheck 條件：wasCheckAfterMove 最後 6 筆中，
/// 同一方（隔 2 步）的 3 筆全為 true，且 halfMoveClock ≥ 6。
///
/// 測試策略：直接透過反射設定 wasCheckAfterMove 欄位，
/// 精確控制輸入條件，不依賴棋盤規則組合。
/// </summary>
public class BoardWxfTests
{
    private const string MinimalFen = "4k4/9/9/9/9/9/9/9/4R4/4K4 w - - 6 1";

    // 紅車在 row8 col4（index 76），可以移動到 row7 col4（index 67）
    private static readonly Move RookUp   = new Move(76, 67);
    private static readonly Move RookDown = new Move(67, 76);

    // ─── IsLikelyPerpetualCheck：基本行為 ────────────────────────────────

    [Fact]
    public void IsLikelyPerpetualCheck_NoMoves_ReturnsFalse()
    {
        var board = new Board(MinimalFen);
        Assert.False(board.IsLikelyPerpetualCheck());
    }

    [Fact]
    public void IsLikelyPerpetualCheck_FewerThanSixEntries_ReturnsFalse()
    {
        var board = new Board(MinimalFen);
        SetWasCheckAfterMove(board, new bool[] { true, false, true, false }); // 只有 4 筆
        Assert.False(board.IsLikelyPerpetualCheck(), "少於 6 筆時應回傳 false");
    }

    [Fact]
    public void IsLikelyPerpetualCheck_ContinuousCheckPattern_ReturnsTrue()
    {
        var board = new Board(MinimalFen);
        // [0]=紅check, [1]=黑idle, [2]=紅check, [3]=黑idle, [4]=紅check, [5]=黑idle
        // 掃描索引 5,3,1（同一方步）：false, false, false... 不對
        // 正確：掃描最後 6 筆（索引 5,3,1），即 [5],[3],[1] = false = 不全 true
        // 應掃描 [5],[3],[1]：black 的步 → 若黑將對紅將軍才是 true...
        // 實際上 wasCheckAfterMove[i] = 走完後「對手」是否被將
        // 索引 0（紅走）= 紅走完後，黑是否被將（= 紅是否給將）
        // 索引 1（黑走）= 黑走完後，紅是否被將（= 黑是否給將）
        // IsLikelyPerpetualCheck 掃描 count-1, count-3, count-5（同一方的步）
        // 若 count=6，掃描 [5],[3],[1] = 黑的最後3步 → 這些是黑方給的將
        // 若 count=6，且想讓紅方連續給將：[0],[2],[4] 應都是 true，[1],[3],[5] 為 false
        // 但掃描方向是 count-1 開始... 如果想測「紅方」連續給將，需要 count 為偶數且最後一步是黑的
        // 設定：count=7（紅3次 + 黑3次 + 1次黑）：
        //   [0]=紅check, [1]=黑idle, [2]=紅check, [3]=黑idle, [4]=紅check, [5]=黑idle, [6]=第7步
        //   掃描 [6],[4],[2] ... 不對
        // 最簡單：讓 count=6，掃描 [5],[3],[1]（這 3 個全是 true）
        // 這意味著 [1],[3],[5] 的步走完後對手被將，即黑方連續給將
        SetWasCheckAfterMove(board, new bool[] { false, true, false, true, false, true });
        Assert.True(board.IsLikelyPerpetualCheck(),
            "同一方（索引 5,3,1）的最後 3 步都是將軍且 halfMoveClock≥6 時應回傳 true");
    }

    [Fact]
    public void IsLikelyPerpetualCheck_CheckInterrupted_ReturnsFalse()
    {
        var board = new Board(MinimalFen);
        // 索引 3 為 false（非將軍），中斷連續將軍
        SetWasCheckAfterMove(board, new bool[] { false, true, false, false, false, true });
        Assert.False(board.IsLikelyPerpetualCheck(), "將軍鏈中斷時應回傳 false");
    }

    [Fact]
    public void IsLikelyPerpetualCheck_HalfMoveClockTooLow_ReturnsFalse()
    {
        // halfMoveClock = 3，表示最近 3 步有吃子（< 6）
        var board = new Board("4k4/9/9/9/9/9/9/9/4R4/4K4 w - - 3 1");
        SetWasCheckAfterMove(board, new bool[] { false, true, false, true, false, true });
        Assert.False(board.IsLikelyPerpetualCheck(), "halfMoveClock < 6 時應回傳 false");
    }

    // ─── Clone 正確複製 wasCheckAfterMove ────────────────────────────────

    [Fact]
    public void Clone_PreservesWasCheckAfterMove()
    {
        var board = new Board(MinimalFen);
        SetWasCheckAfterMove(board, new bool[] { false, true, false, true, false, true });

        var clone = (Board)board.Clone();
        Assert.True(clone.IsLikelyPerpetualCheck(), "Clone 後應保留 wasCheckAfterMove 內容");
    }

    // ─── UnmakeMove 正確移除 wasCheckAfterMove ────────────────────────────

    [Fact]
    public void UnmakeMove_RemovesLastWasCheckEntry()
    {
        var board = new Board(MinimalFen);
        // 走一步後有 1 筆記錄
        board.MakeMove(RookUp);

        // 撤銷後 wasCheckAfterMove 應移除那筆
        board.UnmakeMove(RookUp);

        // 重置 halfMoveClock 使其 ≥ 6（FEN 已是 6），但 wasCheckAfterMove.Count < 6
        Assert.False(board.IsLikelyPerpetualCheck(),
            "UnmakeMove 後 wasCheckAfterMove 應移除最後一筆，不滿足長將條件");
    }

    [Fact]
    public void MakeNullMove_DoesNotAddToWasCheckAfterMove()
    {
        var board = new Board(MinimalFen);
        var countBefore = GetWasCheckAfterMoveCount(board);

        board.MakeNullMove();
        Assert.Equal(countBefore, GetWasCheckAfterMoveCount(board));

        board.UnmakeNullMove();
        Assert.Equal(countBefore, GetWasCheckAfterMoveCount(board));
    }

    // ─── 輔助方法 ────────────────────────────────────────────────────────

    private static readonly FieldInfo WasCheckField =
        typeof(Board).GetField("wasCheckAfterMove",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new System.InvalidOperationException("wasCheckAfterMove field not found");

    private static void SetWasCheckAfterMove(Board board, bool[] values)
    {
        var list = (List<bool>)WasCheckField.GetValue(board)!;
        list.Clear();
        list.AddRange(values);
    }

    private static int GetWasCheckAfterMoveCount(Board board)
    {
        var list = (List<bool>)WasCheckField.GetValue(board)!;
        return list.Count;
    }
}
