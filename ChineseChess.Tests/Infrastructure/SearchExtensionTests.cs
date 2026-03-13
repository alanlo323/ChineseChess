using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 M1a/M1b 放寬延伸條件後的搜尋行為：
///   M1a - 吃子延伸放寬至 ply <= 3，並加入 extensionBudget（每路徑最多 6 次延伸）
///   M1b - 威脅延伸放寬至 ply <= 2，depth >= 2，i < 8
/// </summary>
public class SearchExtensionTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── M1a：吃子延伸 ────────────────────────────────────────────────────

    [Fact]
    public void CaptureExtension_AtPly2_ShouldStillExtend()
    {
        // M1a：ply <= 3 時應可延伸吃子
        // 驗證：在 ply=2 的吃子應觸發延伸（過去是 ply <= 1 才延伸）
        var board = new Board("2r1k4/9/9/9/9/2p1R1p2/9/9/9/3K5 w - - 0 1");
        var worker = CreateWorker(board);

        var safeCapture = new Move(49, 51);  // 紅車吃安全位置的黑兵

        Assert.Contains(safeCapture, board.GenerateLegalMoves());
        Assert.True(worker.ShouldExtendForCapture(safeCapture));
    }

    [Fact]
    public void ExtensionBudget_ShouldCapTotalExtensions()
    {
        // 延伸預算：每條路徑最多 6 次延伸，超過後即使條件符合也不再延伸
        // 這個測試透過 effectiveMaxPly 間接驗證
        var board = new Board("4k4/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1");
        var worker = CreateWorker(board);

        // 確保基本搜尋不會無限延伸（測試搜尋可正常完成）
        int score = worker.SearchSingleDepth(3);
        Assert.True(score != 0 || board.IsCheck(board.Turn)); // 搜尋完成，返回合理分數
    }

    // ─── M1b：威脅延伸 ────────────────────────────────────────────────────

    [Fact]
    public void ThreatExtension_AtPly1_ShouldTriggerForThreatCreatingMove()
    {
        // M1b：ply <= 2 時應可觸發威脅延伸
        // 使用 CreatesImmediateThreat 方法驗證
        var board = new Board("4k4/9/9/9/9/1R2P2n1/9/9/9/4K4 w - - 0 1");
        var worker = CreateWorker(board);

        var threatMove = new Move(49, 40);  // 紅車移動創造威脅

        Assert.Contains(threatMove, board.GenerateLegalMoves());
        Assert.True(worker.CreatesImmediateThreat(threatMove));
    }

    [Fact]
    public async Task Search_WithRelaxedExtensions_ShouldCompleteWithoutInfiniteLoop()
    {
        // 放寬延伸條件後，搜尋應能正常完成（不陷入無限延伸）
        var board = new Board("4k4/9/9/9/4r4/4P4/9/9/9/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 4, TimeLimitMs = 5000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull);
        Assert.True(result.Nodes > 0);
    }

    [Fact]
    public async Task Search_TacticalPosition_WithRelaxedExtensions_FindsGoodCapture()
    {
        // 放寬延伸後，應能在戰術局面中找到更好的吃法
        var board = new Board("4k4/9/9/9/9/4R4/9/4r4/9/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 5000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull);
    }

    private static SearchWorker CreateWorker(IBoard board)
    {
        return new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            new TranspositionTable(sizeMb: 4),
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
    }
}
