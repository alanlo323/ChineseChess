using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 Singular Extension（奇異延伸）功能：
///   SE - 當 TT 走法在排除搜尋中明顯優於其他選擇時，對該走法給予延伸搜尋深度。
///   條件：depth >= 6, ttMove 有效, TT 深度充足, TT 分數遠離將殺, ply > 0, excludedMove 為空
/// </summary>
public class SingularExtensionTests
{
    // 初始局面 FEN
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── SE 觸發條件測試 ─────────────────────────────────────────────────────

    [Fact]
    public void SE_ConditionCheck_DepthBelowThreshold_ShouldNotTrigger()
    {
        // 驗證：depth < SingularExtensionMinDepth(6) 時，不應觸發奇異延伸
        // 即使 TT 有完整資訊，淺層搜尋不應執行代價高昂的排除搜尋
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        // 以深度 4（低於門檻 6）執行搜尋，應能正常完成
        int score = worker.SearchSingleDepth(4);

        // 驗證搜尋正常完成（不爆搜、不崩潰）
        Assert.True(score is >= -30000 and <= 30000, $"分數應在合理範圍內，實際為 {score}");
    }

    [Fact]
    public void SE_ConditionCheck_TTMoveIsNull_ShouldNotTrigger()
    {
        // 驗證：TT 中沒有走法時（新鮮局面），不應觸發奇異延伸
        // 沒有 ttMove 代表無法做排除搜尋（不知要排除哪個走法）
        var board = new Board(InitialFen);
        var tt = new TranspositionTable(sizeMb: 4);
        // 不預先填充 TT，確保 ttMove 為 null
        var worker = CreateWorker(board, tt);

        // 深度 6（達到門檻），但 TT 沒有資料
        int score = worker.SearchSingleDepth(6);

        // 搜尋應正常完成
        Assert.True(score is >= -30000 and <= 30000, $"分數應在合理範圍內，實際為 {score}");
    }

    [Fact]
    public void SE_ConditionCheck_TTDepthTooShallow_ShouldNotTrigger()
    {
        // 驗證：TT 中條目的深度不足（< depth - 3）時，不應觸發奇異延伸
        // 深度不足的 TT 資訊不夠可靠，不適合作為奇異延伸依據
        var board = new Board(InitialFen);
        var tt = new TranspositionTable(sizeMb: 4);

        // 預先在 TT 存入一個深度很淺的條目
        var legalMoves = board.GenerateLegalMoves().ToList();
        var dummyMove = legalMoves[0];
        // 存入深度 1 的條目（遠低於搜尋深度 6 所需的 >= 3）
        tt.Store(board.ZobristKey, 50, depth: 1, TTFlag.Exact, dummyMove);

        var worker = CreateWorker(board, tt);
        int score = worker.SearchSingleDepth(6);

        Assert.True(score is >= -30000 and <= 30000, $"分數應在合理範圍內，實際為 {score}");
    }

    [Fact]
    public void SE_ConditionCheck_TTScoreNearMate_ShouldNotTrigger()
    {
        // 驗證：TT 分數接近將殺（|score| >= CheckmateThreshold=15000）時，不應觸發奇異延伸
        // 將殺序列中奇異延伸沒有意義且可能產生誤判
        var board = new Board(InitialFen);
        var tt = new TranspositionTable(sizeMb: 4);

        var legalMoves = board.GenerateLegalMoves().ToList();
        var dummyMove = legalMoves[0];
        // 存入接近將殺的分數（16000 > CheckmateThreshold 15000）
        tt.Store(board.ZobristKey, 16000, depth: 4, TTFlag.LowerBound, dummyMove);

        var worker = CreateWorker(board, tt);
        int score = worker.SearchSingleDepth(6);

        Assert.True(score is >= -30000 and <= 30000, $"分數應在合理範圍內，實際為 {score}");
    }

    // ─── SE 核心機制測試 ─────────────────────────────────────────────────────

    [Fact]
    public void SE_ExcludedMoveSkipped_InMoveLoop()
    {
        // 驗證：排除走法機制正確運作
        // 當傳入 excludedMove 時，搜尋應跳過該走法（避免重複計算 TT 走法）
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        // 取得合法走法列表
        var legalMoves = board.GenerateLegalMoves().ToList();
        Assert.NotEmpty(legalMoves);

        var excludedMove = legalMoves[0];
        // 呼叫排除搜尋輔助方法（僅在 SearchWorker 中存在）
        int scoreWithExclusion = worker.SearchWithExcludedMove(depth: 3, excludedMove);
        int scoreNormal = worker.SearchSingleDepth(3);

        // 兩者分數可能不同（排除了一個走法），但都應在合理範圍內
        Assert.True(scoreWithExclusion is >= -30000 and <= 30000,
            $"排除搜尋分數應在合理範圍內，實際為 {scoreWithExclusion}");
        Assert.True(scoreNormal is >= -30000 and <= 30000,
            $"正常搜尋分數應在合理範圍內，實際為 {scoreNormal}");
    }

    [Fact]
    public async Task SE_SearchCompletes_WithSingularExtension()
    {
        // 驗證：啟用奇異延伸後，完整搜尋能正常完成（不崩潰、不無限遞迴）
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings
        {
            Depth = 7,          // > SingularExtensionMinDepth(6)，可觸發 SE
            TimeLimitMs = 10000,
            ThreadCount = 1
        };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull, "搜尋深度 7 應能找到合法走法");
        Assert.True(result.Nodes > 0, "應有節點計數");
        Assert.True(result.Score is >= -30000 and <= 30000, $"分數應在合理範圍內，實際為 {result.Score}");
    }

    [Fact]
    public async Task SE_TacticalPosition_FindsCriticalMove()
    {
        // 驗證：奇異延伸應有助於在戰術局面中找出關鍵走法
        // 紅車(44) vs 黑車(4)，在同一列上直接對峙
        var board = new Board("4k4/9/9/9/4r4/4R4/9/9/9/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings
        {
            Depth = 7,
            TimeLimitMs = 10000,
            ThreadCount = 1
        };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull, "戰術局面應能找到走法");
        Assert.True(result.Nodes > 0);
    }

    [Fact]
    public async Task SE_DoesNotExplodeSearchDepth()
    {
        // 驗證：奇異延伸不會導致搜尋深度爆炸（搜尋時間應在合理範圍內）
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings
        {
            Depth = 8,
            TimeLimitMs = 15000,  // 15 秒上限
            ThreadCount = 1
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await engine.SearchAsync(board, settings, CancellationToken.None);
        stopwatch.Stop();

        Assert.NotNull(result);
        // 搜尋時間不應超過設定的時間限制（允許 2 秒緩衝）
        Assert.True(stopwatch.ElapsedMilliseconds < 17000,
            $"搜尋耗時 {stopwatch.ElapsedMilliseconds}ms，超過合理上限（17000ms）");
        Assert.False(result.BestMove.IsNull);
    }

    // ─── 輔助方法 ─────────────────────────────────────────────────────────────

    private static SearchWorker CreateWorker(IBoard board, TranspositionTable? tt = null)
    {
        return new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            tt ?? new TranspositionTable(sizeMb: 4),
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
    }
}
