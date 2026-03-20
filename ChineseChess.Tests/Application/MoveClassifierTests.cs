using ChineseChess.Application.Enums;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// MoveClassifier.Classify() 的單元測試。
///
/// 測試用局面說明（棋盤索引 index = row * 9 + col，row 0 = 黑方底線）：
///
///   基礎 FEN（無吃子）：halfMoveClock=5 表示無威脅的 Idle 測試
///
///   [吃子/取消測試]：在 move.To 放置對方棋子，觸發 Cancel
///
///   [將軍測試] checkFen：
///     "k8/9/9/9/9/9/9/9/4R4/8K w - - 0 1"
///     黑將 row0 col0 (0)，紅車 row8 col4 (76)，紅帥 row9 col8 (89)
///     紅車 76→9 (row1 col4)：黑將在 col0，車在 col4，不同列，不將軍
///     改用：紅車 76→0+跨過... 不行（吃子）
///     使用 CheckFen：紅車在 col0 row8 (72)，黑將在 row0 col0 (0)
///     紅車 72→9 (row1 col0)：同列 col0，黑將在 row0，中間 row1，車在 row1 → 將軍！
///
///   [追擊測試] chaseFen：
///     "3k5/9/9/9/9/R7n/9/9/9/4K4 w - - 0 1"
///     黑將 row0 col3 (3)，紅車 row5 col0 (45)，黑馬 row5 col8 (53)，紅帥 row9 col4 (85)
///     紅車 45→49 (row5 col4)：Black 輪次，車威脅馬（同列），馬未受保護 → Chase
///
///   [保護測試] protectedFen：
///     "3k5/9/9/9/9/R7c/9/8r/9/4K4 w - - 0 1"
///     黑將(3)，紅車(45)，黑炮 row5 col8 (53)，黑車 row7 col8 (71)，紅帥(85)
///     紅車 45→49：車威脅炮，黑車可回吃（600≤600=受保護）→ Idle
///
///   [多重威脅測試] multiThreatFen：
///     "k8/4c4/9/9/9/R7n/9/9/9/8K w - - 0 1"
///     黑將(0)，黑炮 row1 col4 (13)，紅車 row5 col0 (45)，黑馬 row5 col8 (53)，紅帥(89)
///     紅車 45→49：威脅炮(col4往上)和馬(row5往右)，2個目標 → Idle
/// </summary>
public class MoveClassifierTests
{
    // ─── 1. 吃子 → Cancel ────────────────────────────────────────────────

    [Fact]
    public void Classify_Capture_ReturnsCancel()
    {
        // 紅車 row9 col0 (81) 吃黑炮 row5 col0 (45)
        var board = new Board("4k4/9/9/9/9/c8/9/9/9/R3K4 w - - 3 1");
        var movedPiece    = board.GetPiece(81);
        var capturedPiece = board.GetPiece(45);
        board.MakeMove(new Move(81, 45));

        var cls = MoveClassifier.Classify(board, new Move(81, 45), movedPiece, capturedPiece, out _);
        Assert.Equal(MoveClassification.Cancel, cls);
    }

    // ─── 2. 兵前進 → Cancel ──────────────────────────────────────────────

    [Fact]
    public void Classify_PawnAdvance_ReturnsCancel()
    {
        // 紅兵 row4 col4 (40)，向上前進到 row3 col4 (31)（紅兵前進 = row 減小）
        var board = new Board("4k4/9/9/9/4P4/9/9/9/9/4K4 w - - 0 1");
        var movedPiece    = board.GetPiece(40);
        var capturedPiece = board.GetPiece(31);
        board.MakeMove(new Move(40, 31));

        var cls = MoveClassifier.Classify(board, new Move(40, 31), movedPiece, capturedPiece, out _);
        Assert.Equal(MoveClassification.Cancel, cls);
    }

    // ─── 3. 兵橫移 → Idle（不是 Cancel）────────────────────────────────

    [Fact]
    public void Classify_PawnLateral_ReturnsIdle()
    {
        // 紅兵 row4 col4 (40)，已過河（紅兵過河 ≤ row4），橫移到 row4 col5 (41)
        // 黑將 row0 col0 (0)，紅帥 row9 col8 (89)，兩將不對齊（無飛將問題）
        var board = new Board("k8/9/9/9/4P4/9/9/9/9/8K w - - 0 1");
        var movedPiece    = board.GetPiece(40);
        var capturedPiece = board.GetPiece(41);
        board.MakeMove(new Move(40, 41));

        var cls = MoveClassifier.Classify(board, new Move(40, 41), movedPiece, capturedPiece, out _);
        // 兵橫移不是前進，不吃子，不將軍，不追擊 → Idle
        Assert.Equal(MoveClassification.Idle, cls);
    }

    // ─── 4. 走完後將軍 → Check ─────────────────────────────────────────

    [Fact]
    public void Classify_GivesCheck_ReturnsCheck()
    {
        // FEN：黑將 row0 col0 (0)，紅車 row8 col0 (72)，紅帥 row9 col4 (85)
        // 紅車 72→9 (row1 col0)：同列 col0，中間一格 row1，車到 row1 → 黑將在 row0 被將
        var board = new Board("k8/9/9/9/9/9/9/9/R8/4K4 w - - 0 1");
        var movedPiece    = board.GetPiece(72);
        var capturedPiece = board.GetPiece(9);
        board.MakeMove(new Move(72, 9));

        var cls = MoveClassifier.Classify(board, new Move(72, 9), movedPiece, capturedPiece, out _);
        Assert.Equal(MoveClassification.Check, cls);
    }

    // ─── 5. 車追未保護馬 → Chase ─────────────────────────────────────────

    [Fact]
    public void Classify_RookThreatensUnprotectedHorse_ReturnsChase()
    {
        // FEN：黑將(3)，紅車(45)，黑馬 row5 col8 (53)，紅帥(85)
        // 紅車 45→49 (row5 col4)：同列威脅馬，馬無保護 → Chase
        var board = new Board("3k5/9/9/9/9/R7n/9/9/9/4K4 w - - 0 1");
        var movedPiece    = board.GetPiece(45);
        var capturedPiece = board.GetPiece(49);
        board.MakeMove(new Move(45, 49));

        var cls = MoveClassifier.Classify(board, new Move(45, 49), movedPiece, capturedPiece, out int victim);
        Assert.Equal(MoveClassification.Chase, cls);
        Assert.Equal(53, victim); // 黑馬在 index 53
    }

    // ─── 6. 車追受保護炮 → Idle ────────────────────────────────────────────

    [Fact]
    public void Classify_RookThreatensProtectedCannon_ReturnsIdle()
    {
        // FEN：黑將(3)，紅車(45)，黑炮 row5 col8 (53)，黑車 row7 col8 (71)，紅帥(85)
        // 紅車 45→49：威脅黑炮，黑車可回吃（600≤600，有效保護）→ Idle
        var board = new Board("3k5/9/9/9/9/R7c/9/8r/9/4K4 w - - 0 1");
        var movedPiece    = board.GetPiece(45);
        var capturedPiece = board.GetPiece(49);
        board.MakeMove(new Move(45, 49));

        var cls = MoveClassifier.Classify(board, new Move(45, 49), movedPiece, capturedPiece, out _);
        Assert.Equal(MoveClassification.Idle, cls);
    }

    // ─── 7. 同時威脅兩子 → Idle ────────────────────────────────────────────

    [Fact]
    public void Classify_ThreatensMultiplePieces_ReturnsIdle()
    {
        // FEN：黑將(0)，黑炮 row1 col4 (13)，紅車(45)，黑馬(53)，紅帥(89)
        // 紅車 45→49：威脅黑炮（col4 往上）和黑馬（row5 往右），2 個候選 → Idle
        var board = new Board("k8/4c4/9/9/9/R7n/9/9/9/8K w - - 0 1");
        var movedPiece    = board.GetPiece(45);
        var capturedPiece = board.GetPiece(49);
        board.MakeMove(new Move(45, 49));

        var cls = MoveClassifier.Classify(board, new Move(45, 49), movedPiece, capturedPiece, out _);
        Assert.Equal(MoveClassification.Idle, cls);
    }

    // ─── 8. 皮卡魚例外規則：以弱換強仍算捉 ────────────────────────────────
    // 例外1：馬/炮 攻擊 受保護車 → Chase（車值600 >> 馬270/炮285）

    [Fact]
    public void Classify_CannonThreatensProtectedRook_ReturnsChase()
    {
        // 局面：黑將(3)，黑馬 row3 col7 (34)=保護者，
        //   紅炮 row5 col0 (45)，黑仕(炮台) row5 col5 (50)，黑車 row5 col8 (53)，紅帥(85)
        // 紅炮 45→47：移至 col2，經仕(炮台 col5) 威脅黑車 col8
        // 黑車受黑馬(34→53)保護(270≤285)，但炮捉車屬於例外1 → Chase
        var board = new Board("3k5/9/9/7n1/9/C4a2r/9/9/9/4K4 w - - 0 1");
        var movedPiece    = board.GetPiece(45);
        var capturedPiece = board.GetPiece(47);
        board.MakeMove(new Move(45, 47));

        var cls = MoveClassifier.Classify(board, new Move(45, 47), movedPiece, capturedPiece, out int victim);
        Assert.Equal(MoveClassification.Chase, cls);
        Assert.Equal(53, victim); // 黑車在 index 53
    }

    [Fact]
    public void Classify_HorseThreatensProtectedRook_ReturnsChase()
    {
        // 局面：黑將(3)，黑馬(保護者) row3 col2 (29)，
        //   黑車 row5 col3 (48)，紅帥(85)，紅馬 row9 col5 (86)
        // 紅馬 86→67 (row7 col4)：可吃黑車 48（馬步 (7,4)→(5,3)）
        // 黑車受黑馬 29→48 保護(270≤270)，但馬捉車屬於例外1 → Chase
        var board = new Board("3k5/9/9/2n6/9/3r5/9/9/9/4KN3 w - - 0 1");
        var movedPiece    = board.GetPiece(86);
        var capturedPiece = board.GetPiece(67);
        board.MakeMove(new Move(86, 67));

        var cls = MoveClassifier.Classify(board, new Move(86, 67), movedPiece, capturedPiece, out int victim);
        Assert.Equal(MoveClassification.Chase, cls);
        Assert.Equal(48, victim); // 黑車在 index 48
    }

    // 例外2：士/象 攻擊 受保護馬/炮/車 → Chase（士象值120 << 馬270/炮285/車600）

    [Fact]
    public void Classify_AdvisorThreatensProtectedRook_ReturnsChase()
    {
        // 局面：黑將(3)，黑車 row7 col3 (66)，黑卒 row7 col4 (67)=保護者
        //   紅仕 row9 col3 (84)，紅帥 row9 col4 (85)
        // 紅仕 84→76 (row8 col4)：可吃黑車 66（對角線(8,4)→(7,3)）
        // 黑車受黑卒 67→66 保護(30≤120)，但仕捉車屬於例外2 → Chase
        var board = new Board("3k5/9/9/9/9/9/9/3rp4/9/3AK4 w - - 0 1");
        var movedPiece    = board.GetPiece(84);
        var capturedPiece = board.GetPiece(76);
        board.MakeMove(new Move(84, 76));

        var cls = MoveClassifier.Classify(board, new Move(84, 76), movedPiece, capturedPiece, out int victim);
        Assert.Equal(MoveClassification.Chase, cls);
        Assert.Equal(66, victim); // 黑車在 index 66
    }

    [Fact]
    public void Classify_ElephantThreatensProtectedCannon_ReturnsChase()
    {
        // 局面：黑將(3)，黑卒 row5 col1 (46)=保護者，黑炮 row5 col2 (47)
        //   紅象 row9 col2 (83)，紅帥 row9 col4 (85)
        // 紅象 83→67 (row7 col4)：可吃黑炮 47（象步(7,4)→(5,2)，象眼(6,3)空）
        // 黑炮受黑卒 46→47 保護(30≤120)，但象捉炮屬於例外2 → Chase
        var board = new Board("3k5/9/9/9/9/1pc6/9/9/9/2B1K4 w - - 0 1");
        var movedPiece    = board.GetPiece(83);
        var capturedPiece = board.GetPiece(67);
        board.MakeMove(new Move(83, 67));

        var cls = MoveClassifier.Classify(board, new Move(83, 67), movedPiece, capturedPiece, out int victim);
        Assert.Equal(MoveClassification.Chase, cls);
        Assert.Equal(47, victim); // 黑炮在 index 47
    }
}
