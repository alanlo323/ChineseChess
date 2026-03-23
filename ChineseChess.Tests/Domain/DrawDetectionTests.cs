using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 和棋判定功能的完整單元測試套件。
///
/// 測試範疇：
/// 1. 重覆局面判定（IsDrawByRepetition）
/// 2. 無吃子步數判定（IsDrawByNoCapture / HalfMoveClock）
/// 3. 綜合和棋判定（IsDraw）
/// 4. Undo/Unmake 後歷史還原
/// 5. Clone 後歷史複製
/// 6. MakeNullMove 不記錄歷史
/// 7. ParseFen 重置歷史
/// 8. 邊界條件
///
/// 棋盤索引：index = row * 9 + col（row 0 = 上方黑方底線，row 9 = 下方紅方底線）
///
/// LoopFen 局面說明：
///   "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1"
///   黑方：將 e10(4)、仕 f10(5)，紅方：車 a1(81)、帥 d1(84)
///   合法循環走法：
///     紅車 a1(81) ↔ a2(72)
///     黑仕 f10(5) ↔ e9(13)
/// </summary>
public class DrawDetectionTests
{
    private const string MinimalFen = "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1";
    private const string LoopFen    = "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1";

    // 循環走法（合法且對稱）
    private static readonly Move RedRookDown      = new Move(81, 72); // 紅車 a1→a2
    private static readonly Move RedRookUp        = new Move(72, 81); // 紅車 a2→a1
    private static readonly Move BlackAdvisorDown = new Move(5, 13);  // 黑仕 f10→e9
    private static readonly Move BlackAdvisorUp   = new Move(13, 5);  // 黑仕 e9→f10

    /// <summary>執行一次完整的循環（4 步，局面回到起點）。</summary>
    private static void DoOneCycle(Board board)
    {
        board.MakeMove(RedRookDown);
        board.MakeMove(BlackAdvisorDown);
        board.MakeMove(RedRookUp);
        board.MakeMove(BlackAdvisorUp);
    }

    // ─── IsDrawByRepetition：基本行為 ────────────────────────────────────

    [Fact]
    public void IsDrawByRepetition_WhenNoRepetition_ReturnsFalse()
    {
        var board = new Board(MinimalFen);
        Assert.False(board.IsDrawByRepetition());
    }

    [Fact]
    public void IsDrawByRepetition_WhenPositionRepeatedThreeTimes_ReturnsTrue()
    {
        // 初始局面算第一次出現；兩個完整循環後局面第三次出現
        var board = new Board(LoopFen);

        Assert.False(board.IsDrawByRepetition(), "起始局面只出現一次，不應觸發和棋");

        DoOneCycle(board); // 局面第二次出現
        Assert.False(board.IsDrawByRepetition(), "第二次出現不應觸發和棋");

        DoOneCycle(board); // 局面第三次出現
        Assert.True(board.IsDrawByRepetition(), "第三次出現應觸發和棋");
    }

    [Fact]
    public void IsDrawByRepetition_AfterOneCycle_ReturnsFalse()
    {
        var board = new Board(LoopFen);
        DoOneCycle(board); // 局面只出現兩次
        Assert.False(board.IsDrawByRepetition());
    }

    // ─── IsDrawByRepetition：自訂閾值 ────────────────────────────────────

    [Fact]
    public void IsDrawByRepetition_CustomThreshold_RespectedCorrectly()
    {
        var board = new Board(LoopFen);

        DoOneCycle(board);
        Assert.True(board.IsDrawByRepetition(threshold: 2));
        Assert.False(board.IsDrawByRepetition(threshold: 3));

        DoOneCycle(board);
        Assert.True(board.IsDrawByRepetition(threshold: 3));
    }

    // ─── IsDrawByRepetition：Undo 還原 ───────────────────────────────────

    [Fact]
    public void IsDrawByRepetition_AfterUndo_ReturnsFalse()
    {
        // 達到三次重覆後悔棋，應還原到未達和棋狀態
        var board = new Board(LoopFen);
        DoOneCycle(board);
        DoOneCycle(board);

        Assert.True(board.IsDrawByRepetition(), "悔棋前應觸發和棋");

        // 悔棋一步後局面只出現兩次（黑仕未回到原位）
        board.UnmakeMove(BlackAdvisorUp);
        Assert.False(board.IsDrawByRepetition(), "悔棋後不應再觸發和棋");
    }

    [Fact]
    public void HalfMoveClock_AfterUnmake_RestoresPreviousValue()
    {
        var board = new Board(LoopFen);

        Assert.Equal(0, board.HalfMoveClock);

        board.MakeMove(RedRookDown);
        Assert.Equal(1, board.HalfMoveClock);

        board.MakeMove(BlackAdvisorDown);
        Assert.Equal(2, board.HalfMoveClock);

        board.UnmakeMove(BlackAdvisorDown);
        Assert.Equal(1, board.HalfMoveClock);

        board.UnmakeMove(RedRookDown);
        Assert.Equal(0, board.HalfMoveClock);
    }

    // ─── IsDrawByNoCapture：基本行為 ─────────────────────────────────────

    [Fact]
    public void IsDrawByNoCapture_WithZeroHalfMoveClock_ReturnsFalse()
    {
        var board = new Board(MinimalFen);
        Assert.Equal(0, board.HalfMoveClock);
        Assert.False(board.IsDrawByNoCapture());
    }

    [Fact]
    public void IsDrawByNoCapture_AfterFiftyNineNonCaptureMoves_ReturnsFalse()
    {
        var board = new Board(LoopFen);

        // 14 個完整循環 = 56 步，再加 3 步 = 59 步無吃子
        for (int i = 0; i < 14; i++)
        {
            board.MakeMove(RedRookDown);
            board.MakeMove(BlackAdvisorDown);
            board.MakeMove(RedRookUp);
            board.MakeMove(BlackAdvisorUp);
        }
        board.MakeMove(RedRookDown);
        board.MakeMove(BlackAdvisorDown);
        board.MakeMove(RedRookUp);

        Assert.Equal(59, board.HalfMoveClock);
        Assert.False(board.IsDrawByNoCapture(), "59 步不應觸發和棋");
    }

    [Fact]
    public void IsDrawByNoCapture_AfterOneHundredTwentyNonCaptureMoves_ReturnsTrue()
    {
        var board = new Board(LoopFen);

        // 30 個完整循環 = 120 步無吃子（皮卡魚規則閾值）
        for (int i = 0; i < 30; i++)
        {
            board.MakeMove(RedRookDown);
            board.MakeMove(BlackAdvisorDown);
            board.MakeMove(RedRookUp);
            board.MakeMove(BlackAdvisorUp);
        }

        Assert.Equal(120, board.HalfMoveClock);
        Assert.True(board.IsDrawByNoCapture(), "120 步無吃子應觸發和棋（皮卡魚規則）");
    }

    [Fact]
    public void IsDrawByNoCapture_CustomLimit_RespectedCorrectly()
    {
        var board = new Board(LoopFen);

        // 走 10 步無吃子
        board.MakeMove(RedRookDown);
        board.MakeMove(BlackAdvisorDown);
        board.MakeMove(RedRookUp);
        board.MakeMove(BlackAdvisorUp);
        board.MakeMove(RedRookDown);
        board.MakeMove(BlackAdvisorDown);
        board.MakeMove(RedRookUp);
        board.MakeMove(BlackAdvisorUp);
        board.MakeMove(RedRookDown);
        board.MakeMove(BlackAdvisorDown);

        Assert.Equal(10, board.HalfMoveClock);
        Assert.True(board.IsDrawByNoCapture(limit: 10), "自訂限制 10 應觸發");
        Assert.False(board.IsDrawByNoCapture(limit: 11), "自訂限制 11 不應觸發");
    }

    // ─── HalfMoveClock：吃子重置 ─────────────────────────────────────────

    [Fact]
    public void HalfMoveClock_ResetsAfterCapture()
    {
        // 紅車 a1(81)，黑車 a9(8)，黑將 e10(4)，紅帥 e1(85)
        var board = new Board("4k3r/9/9/9/9/9/9/9/9/R3K4 w - - 0 1");

        board.MakeMove(new Move(81, 72)); // 紅車 a1→a2（無吃子），半回合計數 = 1

        // 確認黑車在 (0,8)=index 8
        var blackRook = board.GetPiece(8);
        Assert.Equal(PieceType.Rook, blackRook.Type);
        Assert.Equal(PieceColor.Black, blackRook.Color);

        board.MakeMove(new Move(72, 8)); // 紅車 a2→a9（吃黑車），計數歸零
        Assert.Equal(0, board.HalfMoveClock);
        Assert.False(board.IsDrawByNoCapture());
    }

    [Fact]
    public void HalfMoveClock_AccumulatesWithoutCapture()
    {
        var board = new Board(LoopFen);

        board.MakeMove(RedRookDown);
        Assert.Equal(1, board.HalfMoveClock);

        board.MakeMove(BlackAdvisorDown);
        Assert.Equal(2, board.HalfMoveClock);

        board.MakeMove(RedRookUp);
        Assert.Equal(3, board.HalfMoveClock);

        board.MakeMove(BlackAdvisorUp);
        Assert.Equal(4, board.HalfMoveClock);
    }

    // ─── IsDraw：綜合判定 ────────────────────────────────────────────────

    [Fact]
    public void IsDraw_ReturnsFalse_WhenNoDrawConditionMet()
    {
        // 使用含有車的局面：有棋子 → 不觸發棋子不足和棋；
        // halfMoveClock=0 → 不觸發一百二十步；無重覆 → 不觸發重覆局面
        var board = new Board(LoopFen);
        Assert.False(board.IsDraw());
    }

    [Fact]
    public void IsDraw_ReturnsTrueOnRepetition()
    {
        var board = new Board(LoopFen);
        DoOneCycle(board);
        DoOneCycle(board);
        Assert.True(board.IsDraw(), "三次重覆局面應觸發和棋");
    }

    [Fact]
    public void IsDraw_ReturnsTrueOnNoCaptureDraw()
    {
        var board = new Board(LoopFen);
        for (int i = 0; i < 30; i++)
        {
            board.MakeMove(RedRookDown);
            board.MakeMove(BlackAdvisorDown);
            board.MakeMove(RedRookUp);
            board.MakeMove(BlackAdvisorUp);
        }
        // 120 步到達（皮卡魚規則閾值），IsDraw 應為 true（任一條件成立）
        Assert.True(board.IsDraw(), "120 步無吃子應觸發和棋（皮卡魚規則）");
    }

    // ─── Clone：歷史複製 ─────────────────────────────────────────────────

    [Fact]
    public void Clone_PreservesHalfMoveClock()
    {
        var board = new Board(LoopFen);
        board.MakeMove(RedRookDown);
        board.MakeMove(BlackAdvisorDown);

        var clone = board.Clone();
        Assert.Equal(board.HalfMoveClock, clone.HalfMoveClock);
    }

    [Fact]
    public void ZobristHistory_IsCopiedOnClone()
    {
        // Clone 後繼續走棋，和棋計數應繼承原棋盤歷史
        var board = new Board(LoopFen);
        board.MakeMove(RedRookDown);
        board.MakeMove(BlackAdvisorDown);

        var clone = board.Clone();

        // 在 clone 上完成第一個循環
        clone.MakeMove(RedRookUp);
        clone.MakeMove(BlackAdvisorUp); // 局面回到初始（第二次出現）

        // 再走一個完整循環（第三次出現）
        clone.MakeMove(RedRookDown);
        clone.MakeMove(BlackAdvisorDown);
        clone.MakeMove(RedRookUp);
        clone.MakeMove(BlackAdvisorUp);

        Assert.True(clone.IsDrawByRepetition(), "Clone 後繼續走棋應繼承重覆局面歷史");

        // 原棋盤不受影響（只走了 2 步）
        Assert.False(board.IsDrawByRepetition());
    }

    // ─── MakeNullMove：不污染歷史 ────────────────────────────────────────

    [Fact]
    public void MakeNullMove_DoesNotPollutZobristHistory()
    {
        // NullMove 在兩種重覆計數狀態下均不應干擾和棋判定
        var board = new Board(LoopFen);

        // 情境 1：threshold=2（局面第二次出現）
        DoOneCycle(board);
        Assert.True(board.IsDrawByRepetition(threshold: 2));
        board.MakeNullMove();
        board.UnmakeNullMove();
        Assert.True(board.IsDrawByRepetition(threshold: 2),
            "NullMove/UnmakeNullMove 不應干擾重覆局面計數（threshold=2）");

        // 情境 2：threshold=3（局面第三次出現）
        DoOneCycle(board);
        Assert.True(board.IsDrawByRepetition());
        board.MakeNullMove();
        board.UnmakeNullMove();
        Assert.True(board.IsDrawByRepetition(),
            "NullMove 不應導致重覆計數消失（threshold=3）");
    }

    // ─── ParseFen：重置歷史 ──────────────────────────────────────────────

    [Fact]
    public void ParseFen_ResetsZobristHistory()
    {
        var board = new Board(LoopFen);

        // 製造三次重覆
        DoOneCycle(board);
        DoOneCycle(board);
        Assert.True(board.IsDrawByRepetition(), "ParseFen 前應已觸發和棋");

        // 重新解析 FEN 應清空歷史
        board.ParseFen(LoopFen);
        Assert.False(board.IsDrawByRepetition(), "ParseFen 後應重置重覆計數");
    }

    [Fact]
    public void ParseFen_ResetsHalfMoveClock()
    {
        var board = new Board(LoopFen);
        DoOneCycle(board);

        Assert.Equal(4, board.HalfMoveClock);

        board.ParseFen(LoopFen);
        Assert.Equal(0, board.HalfMoveClock);
    }

    // ─── 邊界條件 ────────────────────────────────────────────────────────

    [Fact]
    public void IsDrawByNoCapture_ZeroLimit_ReturnsTrueAlways()
    {
        // limit=0 時，任何半回合計數都 >= 0，應永遠為 true
        var board = new Board(MinimalFen);
        Assert.True(board.IsDrawByNoCapture(limit: 0));
    }

    [Fact]
    public void IsDrawByRepetition_AfterCapture_BreaksRepetition()
    {
        // 吃子後 Zobrist Key 改變，即使走法形狀相似也不構成重覆
        // 局面：紅車 a1(81)，黑車 a1 列上方 a3(63)
        var board = new Board("4k4/9/9/9/9/9/9/r8/9/R3K4 w - - 0 1");
        // 黑車在 (7,0)=63

        var redRookForward = new Move(81, 72); // 紅車 a1→a2
        var redRookBack    = new Move(72, 81); // 紅車 a2→a1
        var blackRookDown  = new Move(63, 54); // 黑車 a3→a4（無吃子）
        var blackRookUp    = new Move(54, 63); // 黑車 a4→a3（無吃子）

        board.MakeMove(redRookForward);
        board.MakeMove(blackRookDown);
        board.MakeMove(redRookBack);
        board.MakeMove(blackRookUp); // 局面第二次出現

        Assert.True(board.IsDrawByRepetition(threshold: 2));

        // 吃子：紅車向上吃黑車（row 7, col 0 = index 63）
        board.MakeMove(new Move(81, 63)); // 紅車吃黑車（跨多格）
        // 吃子後 ZobristKey 完全不同
        Assert.Equal(0, board.HalfMoveClock);
        Assert.False(board.IsDrawByRepetition(threshold: 2),
            "吃子後局面全新，不應達到重覆閾值");
    }

    [Fact]
    public void ZobristKey_IsConsistentAfterMakeAndUnmake()
    {
        // 驗證 MakeMove 後 UnmakeMove 能還原相同的 Zobrist Key（重覆判定的基礎）
        var board = new Board(LoopFen);
        var initialKey = board.ZobristKey;

        board.MakeMove(RedRookDown);
        Assert.NotEqual(initialKey, board.ZobristKey);

        board.UnmakeMove(RedRookDown);
        Assert.Equal(initialKey, board.ZobristKey);
    }

    [Fact]
    public void ZobristKey_IsDifferentBetweenSides()
    {
        // 同樣的棋子位置但不同行棋方，Zobrist Key 應不同（SideToMoveKey）
        var boardRed   = new Board("3aka3/9/9/9/9/9/9/9/9/R3K4 w - - 0 1");
        var boardBlack = new Board("3aka3/9/9/9/9/9/9/9/9/R3K4 b - - 0 1");

        Assert.NotEqual(boardRed.ZobristKey, boardBlack.ZobristKey);
    }

    // ─── IsDrawByInsufficientMaterial：棋子不足和棋 ──────────────────────

    [Fact]
    public void IsDrawByInsufficientMaterial_WithOnlyKingAdvisorElephant_ReturnsTrue()
    {
        // 雙方只剩將/帥、士/仕、象/相，符合棋子不足和棋條件
        // FEN：黑方將(4)+仕(3)+象(2,6)，紅方帥(85)+仕(84)+相(83,87)
        var board = new Board("4k4/9/b3a3b/9/9/9/B3A3B/9/9/4K4 w - - 0 1");
        Assert.True(board.IsDrawByInsufficientMaterial(), "雙方只剩將帥仕士象相應觸發棋子不足和棋");
    }

    [Fact]
    public void IsDrawByInsufficientMaterial_WithOnePawn_ReturnsFalse()
    {
        // 任一方有兵/卒，不構成棋子不足
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4KP3 w - - 0 1");
        Assert.False(board.IsDrawByInsufficientMaterial(), "有兵時不應觸發棋子不足和棋");
    }

    [Fact]
    public void IsDrawByInsufficientMaterial_AllPiecesOnBoard_ReturnsFalse()
    {
        // 初始局面（所有棋子）絕不構成棋子不足
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        Assert.False(board.IsDrawByInsufficientMaterial(), "初始局面不應觸發棋子不足和棋");
    }
}
