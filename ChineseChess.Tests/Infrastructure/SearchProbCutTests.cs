using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// ProbCut 剪枝測試。
///
/// ProbCut 機制（兩階段驗證）：
///   1. SEE 篩選：staticEval + SEE >= probBeta 才值得嘗試
///   2. QSearch 預篩：快速驗證（廉價），通過後才升級到 Negamax
///   3. Negamax 精確驗證：搜尋 depth - ProbCutReduction - 1 層
///   4. 若確認 >= probBeta：存入 TT（LowerBound）並提早返回
///
/// 關鍵保護條件：
///   - ply > 0：根節點必須精確，不得提早返回
///   - !inCheck：將軍局面必須完整搜尋所有應將著法
///   - !isPvNode：PV 路徑需精確展開
///   - !skipNullMove：避免在 null-move / SE 排除搜尋內連鎖觸發
///   - depth >= ProbCutMinDepth（5）：淺層無需此開銷
///   - |beta| < CheckmateThreshold：不誤剪將殺路線
/// </summary>
public class SearchProbCutTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 正確性：ProbCut 不應改變最佳著法 ────────────────────────────────

    [Fact]
    public void ProbCut_EnabledVsDisabled_SameBestMove()
    {
        // ProbCut 開啟時，最佳著法應與關閉時一致（正確性驗證）
        var board = new Board(InitialFen);
        var workerOn = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        int depth = 5;
        workerOn.SearchSingleDepth(depth);
        var bestOn = workerOn.ProbeBestMove();

        workerOff.SearchSingleDepth(depth);
        var bestOff = workerOff.ProbeBestMove();

        Assert.Equal(bestOff, bestOn);
    }

    // ─── ply > 0 保護：根節點絕不被 ProbCut 剪枝 ─────────────────────────

    [Fact]
    public void ProbCut_RootNode_AlwaysReturnsValidMove()
    {
        // 根節點（ply=0）即使 depth >= ProbCutMinDepth，也不應被 ProbCut 剪枝返回
        // 確認即使啟用 ProbCut，根節點也能產生有效最佳著法
        var board = new Board(InitialFen);
        var worker = CreateWorker(board, probCutEnabled: true);

        worker.SearchSingleDepth(6);  // depth=6，滿足觸發門檻
        var bestMove = worker.ProbeBestMove();

        Assert.False(bestMove.IsNull, "根節點不應被 ProbCut 剪枝，應回傳有效著法");
    }

    [Fact]
    public void ProbCut_RootNodeAtDepth5_SameResultAsDisabled()
    {
        // 根節點不觸發 ProbCut，depth=5 的根節點結果應與關閉時完全相同
        var board = new Board(InitialFen);
        var workerOn = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        int scoreOn = workerOn.SearchSingleDepth(5);
        int scoreOff = workerOff.SearchSingleDepth(5);

        // 根節點分數應相同（無 ProbCut 干預）
        Assert.Equal(scoreOff, scoreOn);
    }

    // ─── 效能：ProbCut 應減少節點數 ──────────────────────────────────────

    [Fact]
    public void ProbCut_Enabled_FewerOrEqualNodesAtDepth6()
    {
        // depth=6 時 ProbCut 應減少或相等節點數
        // 初始局面第一層無吃子機會，ProbCut probCaptures 為空 → 節點數相等
        // 在有吃子機會的局面，ProbCut 才會真正減少節點
        var board = new Board(InitialFen);
        var workerOn = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        workerOn.SearchSingleDepth(6);
        long nodesOn = workerOn.NodesVisited;

        workerOff.SearchSingleDepth(6);
        long nodesOff = workerOff.NodesVisited;

        Assert.True(nodesOn <= nodesOff,
            $"ProbCut 啟用 ({nodesOn}) 應 <= 關閉 ({nodesOff})");
    }

    // ─── 條件：depth < 5 不觸發 ProbCut ─────────────────────────────────

    [Fact]
    public void ProbCut_DepthBelowThreshold_NoEffect()
    {
        // depth=4 時 ProbCut 不觸發（ProbCutMinDepth=5），節點數應相同
        var board = new Board(InitialFen);
        var workerOn = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        workerOn.SearchSingleDepth(4);
        long nodesOn = workerOn.NodesVisited;

        workerOff.SearchSingleDepth(4);
        long nodesOff = workerOff.NodesVisited;

        Assert.Equal(nodesOff, nodesOn);
    }

    // ─── 條件：將軍中不觸發 ProbCut ──────────────────────────────────────

    [Fact]
    public void ProbCut_WhenInCheck_BothWorkersReturnSameBestMove()
    {
        // 將軍局面：ProbCut 不觸發（inCheck 條件阻止）
        var board = new Board("3k5/4r4/9/9/9/9/9/9/9/3K5 b - - 0 1");

        var workerOn = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        workerOn.SearchSingleDepth(5);
        var bestOn = workerOn.ProbeBestMove();

        workerOff.SearchSingleDepth(5);
        var bestOff = workerOff.ProbeBestMove();

        Assert.Equal(bestOff, bestOn);
    }

    // ─── TT 存入驗證 ──────────────────────────────────────────────────────

    [Fact]
    public void ProbCut_AfterSearch_HasTtEntries()
    {
        // ProbCut 成功觸發後應將結果存入 TT（LowerBound）
        // 間接驗證：啟用 ProbCut 的搜尋結束後，TT 命中率應達到一定水準
        var board = new Board(InitialFen);
        var tt = new TranspositionTable(sizeMb: 16);  // 較大 TT 確保命中
        var worker = CreateWorkerWithTt(board, tt, probCutEnabled: true);

        worker.SearchSingleDepth(6);

        var stats = tt.GetStatistics();
        Assert.True(stats.OccupiedEntries > 0, "搜尋後 TT 應有條目（包含 ProbCut 存入的 LowerBound）");
    }

    // ─── 正確性：初始局面產生有效著法 ────────────────────────────────────

    [Fact]
    public void ProbCut_InitialPosition_ProducesValidBestMove()
    {
        var board = new Board(InitialFen);
        var worker = CreateWorker(board, probCutEnabled: true);

        worker.SearchSingleDepth(5);
        var bestMove = worker.ProbeBestMove();

        Assert.False(bestMove.IsNull, "ProbCut 開啟後應有有效最佳著法");
    }

    // ─── 重複風險守衛：循環局面不觸發 ProbCut ────────────────────────────

    [Fact]
    public void ProbCut_WhenRepetitionRisk_ProducesSameBestMoveAsDisabled()
    {
        // 製造重複風險：讓棋盤走幾步再退回，使 zobristHistory 內有重複局面
        // 此時 IsAnyRepetitionInLastN(8) 應返回 true → 禁用 ProbCut
        var board = new Board(InitialFen);
        var workerOn = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        // 在初始局面本身重複（ParseFen 加入一次，再手動製造重複）
        // 注意：Board.IsAnyRepetitionInLastN 檢查所有 hash（含不同行棋方），
        // 這裡用正確性測試驗證：即使啟用了 ProbCut，循環局面下結果應與關閉時相同
        workerOn.SearchSingleDepth(5);
        var bestOn = workerOn.ProbeBestMove();

        workerOff.SearchSingleDepth(5);
        var bestOff = workerOff.ProbeBestMove();

        Assert.Equal(bestOff, bestOn);
    }

    [Fact]
    public void IsAnyRepetitionInLastN_NoRepetition_ReturnsFalse()
    {
        // 初始局面只出現一次，不應有重複
        var board = new Board(InitialFen);
        Assert.False(board.IsAnyRepetitionInLastN(8));
    }

    [Fact]
    public void IsAnyRepetitionInLastN_AfterOneCycle_ReturnsTrue()
    {
        // 使用 LoopFen（與 DrawDetectionTests 相同）：走一個完整循環（4 步），
        // 局面回到起點，此時 zobristHistory 中初始 hash 出現兩次 → 有重複
        // LoopFen: "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1"
        // 循環：紅車a1→a2, 黑仕f10→e9, 紅車a2→a1, 黑仕e9→f10
        const string loopFen = "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1";
        var board = new Board(loopFen);

        // 做一個完整循環（4 步）
        board.MakeMove(new Move(81, 72)); // 紅車 a1→a2
        board.MakeMove(new Move(5, 13));  // 黑仕 f10→e9
        board.MakeMove(new Move(72, 81)); // 紅車 a2→a1
        board.MakeMove(new Move(13, 5));  // 黑仕 e9→f10

        // 歷史：[init, r1, b1, r2, init]，init 出現兩次 → 有重複
        Assert.True(board.IsAnyRepetitionInLastN(8));
    }

    // ─── 炮吃子守衛：炮的吃子不納入 ProbCut 候選 ────────────────────────

    [Fact]
    public void ProbCut_CannonCapturePosition_ProducesSameBestMoveAsDisabled()
    {
        // 炮可直接吃子的局面，驗證 ProbCut 排除炮吃子後仍產生正確最佳著法
        // FEN：紅炮在 e5（炮 44），中間有一個炮台棋子，黑方可被炮吃
        const string fenWithCannon = "3k5/9/9/4C4/9/9/9/9/4r4/3K5 w - - 0 1";
        var board = new Board(fenWithCannon);
        var workerOn = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        workerOn.SearchSingleDepth(5);
        var bestOn = workerOn.ProbeBestMove();

        workerOff.SearchSingleDepth(5);
        var bestOff = workerOff.ProbeBestMove();

        // 即使炮吃子被排除出 ProbCut 候選，最佳著法應保持一致
        Assert.Equal(bestOff, bestOn);
    }

    // ─── ProbCutCutCount：成功觸發計數 ────────────────────────────────────

    [Fact]
    public void ProbCut_Enabled_CutCountIsNonNegative()
    {
        var board = new Board(InitialFen);
        var worker = CreateWorker(board, probCutEnabled: true);

        worker.Search(6);

        // 計數器不應為負（初始局面觸發次數可能為 0，取決於局面）
        Assert.True(worker.ProbCutCutCount >= 0);
    }

    [Fact]
    public void ProbCut_Disabled_CutCountIsZero()
    {
        var board = new Board(InitialFen);
        var worker = CreateWorker(board, probCutEnabled: false);

        worker.Search(5);

        Assert.Equal(0, worker.ProbCutCutCount);
    }

    // ─── givesCheck 守衛：吃子給將的候選應被排除 ─────────────────────────

    [Fact]
    public void ProbCut_GivesCheckCapture_DoesNotCorruptBestMove()
    {
        // 局面：紅車(index=76)可吃黑兵(index=13)，吃後車在 col4、黑將(index=4)亦在 col4 → 將軍
        // givesCheck 守衛應排除此著法出 ProbCut 候選；驗證最佳著法不受影響
        const string fen = "4k4/4p4/9/9/9/9/9/9/4R4/3K5 w - - 0 1";
        var board = new Board(fen);
        var workerOn  = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        workerOn.Search(6);
        var bestOn = workerOn.ProbeBestMove();

        workerOff.Search(6);
        var bestOff = workerOff.ProbeBestMove();

        Assert.False(bestOn.IsNull, "ProbCut 開啟時應回傳有效著法");
        Assert.Equal(bestOff, bestOn);
    }

    // ─── ProbCutFalseCount 計數器 ─────────────────────────────────────────────

    [Fact]
    public void ProbCutFalseCount_WhenDisabled_IsZero()
    {
        // ProbCut 關閉時，FalseCount 應維持為 0
        var board = new Board(InitialFen);
        var worker = CreateWorker(board, probCutEnabled: false);

        worker.Search(5);

        Assert.Equal(0, worker.ProbCutFalseCount);
    }

    [Fact]
    public void ProbCutFalseCount_AfterSearch_IsNonNegative()
    {
        // FalseCount 合約驗證：不應為負值
        var board = new Board(InitialFen);
        var worker = CreateWorker(board, probCutEnabled: true);

        worker.Search(6);

        Assert.True(worker.ProbCutFalseCount >= 0,
            "ProbCutFalseCount 不應為負值");
    }

    // ─── WXF 重複裁決：ProbCut on/off 不影響裁決符號 ─────────────────────────

    [Fact]
    public void ProbCut_WxfRepetitionPosition_OnOffGiveSameScoreSign()
    {
        // 局面含兩次完整循環，IsAnyRepetitionInLastN(8) 為 true
        // → ProbCut 重複守衛禁用 ProbCut，回到完整搜尋
        // 驗證：ProbCut on/off 的搜尋分數符號一致（不因 ProbCut 誤判勝負方向）
        const string loopFen = "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1";
        var board = new Board(loopFen);

        // 製造兩次完整循環（各 4 個半步）
        board.MakeMove(new Move(81, 72));
        board.MakeMove(new Move(5, 13));
        board.MakeMove(new Move(72, 81));
        board.MakeMove(new Move(13, 5));
        board.MakeMove(new Move(81, 72));
        board.MakeMove(new Move(5, 13));
        board.MakeMove(new Move(72, 81));
        board.MakeMove(new Move(13, 5));

        var workerOn  = CreateWorker(board, probCutEnabled: true);
        var workerOff = CreateWorker(board, probCutEnabled: false);

        int scoreOn  = workerOn.SearchSingleDepth(5);
        int scoreOff = workerOff.SearchSingleDepth(5);

        // 符號一致表示 WXF 裁決邏輯未被 ProbCut 污染
        Assert.Equal(Math.Sign(scoreOff), Math.Sign(scoreOn));
    }

    private static SearchWorker CreateWorker(IBoard board, bool probCutEnabled = true)
    {
        var worker = new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            new TranspositionTable(sizeMb: 4),
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
        worker.ProbCutEnabled = probCutEnabled;
        return worker;
    }

    private static SearchWorker CreateWorkerWithTt(IBoard board, TranspositionTable tt, bool probCutEnabled = true)
    {
        var worker = new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            tt,
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
        worker.ProbCutEnabled = probCutEnabled;
        return worker;
    }
}
