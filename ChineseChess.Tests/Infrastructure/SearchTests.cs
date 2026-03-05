using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

public class SearchTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public async Task Search_ShouldReturnResult()
    {
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 1, TimeLimitMs = 1000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull);
        Assert.True(result.Nodes >= 0);
    }

    [Fact]
    public async Task Search_ShouldReportHeartbeatProgress()
    {
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000, ThreadCount = 1 };
        var reports = new List<SearchProgress>();

        var result = await engine.SearchAsync(
            board,
            settings,
            CancellationToken.None,
            new Progress<SearchProgress>(p => reports.Add(p)));

        Assert.NotNull(result);
        Assert.True(reports.Count > 0);
        Assert.True(reports.Exists(p => p.IsHeartbeat));
        Assert.True(reports.Exists(p => !p.IsHeartbeat));
        Assert.All(reports, p => Assert.True(p.ElapsedMs >= 0));
        Assert.All(reports, p => Assert.True(p.NodesPerSecond >= 0));
    }

    [Fact]
    public async Task Search_AsymmetricPosition_ShouldReturnNonZeroScore()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABN1 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotEqual(0, result.Score);
        Assert.False(result.BestMove.IsNull);
    }

    [Fact]
    public async Task Search_ShouldReturnSameMove_ForSamePosition()
    {
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000, ThreadCount = 1 };

        var first = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);
        var second = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);

        Assert.Equal(first.BestMove, second.BestMove);
    }

    [Fact]
    public async Task Search_WithCaptureQuiescence_ShouldReturnResult()
    {
        var board = new Board("4k4/9/9/9/4p4/4P4/9/9/9/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 1, TimeLimitMs = 3000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull);
    }

    [Fact]
    public async Task Search_IterativeDeepening_DepthIncreasesNodeCount()
    {
        var board = new Board(InitialFen);
        var engine1 = new SearchEngine();
        var engine2 = new SearchEngine();

        var shallow = await engine1.SearchAsync(board, new SearchSettings { Depth = 1, ThreadCount = 1 }, CancellationToken.None);
        var deeper = await engine2.SearchAsync(
            new Board(InitialFen), new SearchSettings { Depth = 3, ThreadCount = 1 }, CancellationToken.None);

        Assert.True(deeper.Nodes > shallow.Nodes);
        Assert.True(deeper.Depth > shallow.Depth);
    }

    [Fact]
    public async Task Search_ShouldCancelByTimeLimit()
    {
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 1,
            ThreadCount = 1
        };
        var stopwatch = Stopwatch.StartNew();

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);
        stopwatch.Stop();

        Assert.NotNull(result);
        Assert.True(result.Depth <= settings.Depth);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public async Task Search_ShouldPauseAndResumeWithPauseSignal()
    {
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        using var pauseSignal = new ManualResetEventSlim(false);
        var settings = new SearchSettings
        {
            Depth = 18,
            TimeLimitMs = 10000,
            ThreadCount = 1,
            PauseSignal = pauseSignal
        };

        var searchTask = engine.SearchAsync(board, settings, CancellationToken.None);
        var completedBeforeResume = await Task.WhenAny(searchTask, Task.Delay(120));

        Assert.NotSame(searchTask, completedBeforeResume);

        pauseSignal.Set();
        var result = await searchTask;

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Search_WhenPaused_TimeLimitShouldNotExitSearch()
    {
        // 複現 bug：暫停中的搜尋，時間限制到期後不應自動退出
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        using var pauseSignal = new ManualResetEventSlim(false); // 一開始就暫停
        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 150, // 短時間限制
            ThreadCount = 1,
            PauseSignal = pauseSignal
        };

        var searchTask = engine.SearchAsync(board, settings, CancellationToken.None);

        // 等比時間限制更久
        await Task.Delay(400);

        // 搜尋應仍在暫停中（未因時間限制退出）
        Assert.False(searchTask.IsCompleted,
            "暫停中的搜尋不應因時間限制而自動退出");

        // 恢復後才應完成
        pauseSignal.Set();
        var result = await searchTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Search_TimeLimitShouldNotCountPauseDuration()
    {
        // 複現 bug：time limit 以掛牆時間計算，暫停期間計時不停
        // 恢復後，time limit 已耗盡，搜尋立即結束，AI 實際思考時間被縮短
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        using var pauseSignal = new ManualResetEventSlim(false); // 一開始就暫停
        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 500, // 500ms 的思考時間
            ThreadCount = 1,
            PauseSignal = pauseSignal
        };

        var searchTask = engine.SearchAsync(board, settings, CancellationToken.None);

        // 暫停 700ms（超過 time limit）
        await Task.Delay(700);

        // 恢復：修復後 AI 應仍有 ~500ms 的思考時間
        var resumeSw = System.Diagnostics.Stopwatch.StartNew();
        pauseSignal.Set();
        await searchTask;
        resumeSw.Stop();

        // 舊行為：恢復後立即結束（< 50ms），因計時器已過期
        // 新行為：繼續搜尋 ~500ms 的實際思考時間
        Assert.True(resumeSw.ElapsedMilliseconds >= 200,
            $"恢復後搜尋應繼續 ~500ms，但只執行了 {resumeSw.ElapsedMilliseconds}ms（計時器誤計暫停時間）");
    }

    [Fact]
    public async Task Search_MidSearchPause_ShouldNotCountPauseDuration()
    {
        // 複現 bug：搜尋進行中暫停後，time limit 仍持續增加
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        using var pauseSignal = new ManualResetEventSlim(true);
        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 350,
            ThreadCount = 1,
            PauseSignal = pauseSignal
        };

        var searchTask = engine.SearchAsync(board, settings, CancellationToken.None);

        await Task.Delay(120);
        Assert.False(searchTask.IsCompleted, "搜尋在暫停前就已完成，無法驗證 mid-search 暫停邏輯");

        pauseSignal.Reset();
        await Task.Delay(500);

        var resumeSw = System.Diagnostics.Stopwatch.StartNew();
        pauseSignal.Set();
        await searchTask.WaitAsync(TimeSpan.FromSeconds(8));
        resumeSw.Stop();

        // Bug 情境下，恢復後多半會立即退出；修復後需持續消耗剩餘時間
        Assert.True(resumeSw.ElapsedMilliseconds >= 120,
            $"恢復後搜尋不應幾乎立即結束（{resumeSw.ElapsedMilliseconds}ms）");
    }

    [Fact]
    public async Task Search_ElapsedTimeShouldNotIncludePausedDuration()
    {
        // 複現 bug：暫停中的時間不應計入 ElapsedMs
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        using var pauseSignal = new ManualResetEventSlim(false); // 一開始就暫停
        using var cts = new CancellationTokenSource();
        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 30000,
            ThreadCount = 1,
            PauseSignal = pauseSignal
        };

        var depthReports = new System.Collections.Concurrent.ConcurrentBag<SearchProgress>();
        var searchTask = engine.SearchAsync(
            board, settings, cts.Token,
            new Progress<SearchProgress>(p => { if (!p.IsHeartbeat) depthReports.Add(p); }));

        // 暫停 600ms
        await Task.Delay(600);

        // 恢復，等第一個 depth 完成
        pauseSignal.Set();
        await Task.Delay(300);

        cts.Cancel();
        try { await searchTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

        Assert.NotEmpty(depthReports);
        var firstElapsed = depthReports.OrderBy(p => p.CurrentDepth).First().ElapsedMs;
        // ElapsedMs 不應包含 600ms 的暫停時間
        Assert.True(firstElapsed < 400,
            $"ElapsedMs ({firstElapsed}ms) 不應包含暫停期間的時間（約 600ms）");
    }

    [Fact]
    public async Task Search_WhenPaused_HeartbeatShouldNotFire()
    {
        // 複現 bug：暫停時心跳計時器不應繼續回報進度，讓使用者誤以為 AI 還在運算
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        using var pauseSignal = new ManualResetEventSlim(false); // 一開始就暫停
        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 10000,
            ThreadCount = 1,
            PauseSignal = pauseSignal
        };
        using var cts = new CancellationTokenSource();
        var heartbeatCount = 0;

        var searchTask = engine.SearchAsync(
            board, settings, cts.Token,
            new Progress<SearchProgress>(p => { if (p.IsHeartbeat) heartbeatCount++; }));

        // 等足夠久讓心跳可能觸發（心跳間隔 100ms）
        await Task.Delay(1200);
        var heartbeatsWhilePaused = heartbeatCount;

        // 停止搜尋
        cts.Cancel();
        try { await searchTask; } catch { }

        // 暫停時不應有心跳回報
        Assert.Equal(0, heartbeatsWhilePaused);
    }

    // --- PST 測試 ---

    [Fact]
    public void PST_RedHorseCenterScoresHigherThanCorner()
    {
        int centerScore = PieceSquareTables.GetScore(PieceType.Horse, PieceColor.Red, 40);
        int cornerScore = PieceSquareTables.GetScore(PieceType.Horse, PieceColor.Red, 81);

        Assert.True(centerScore > cornerScore);
    }

    [Fact]
    public void PST_RedPawnCrossedRiverScoresHigherThanHome()
    {
        int crossedRiver = PieceSquareTables.GetScore(PieceType.Pawn, PieceColor.Red, 36);
        int homeRow = PieceSquareTables.GetScore(PieceType.Pawn, PieceColor.Red, 72);

        Assert.True(crossedRiver > homeRow);
    }

    [Fact]
    public void PST_BlackMirrorsRed()
    {
        int redCenter = PieceSquareTables.GetScore(PieceType.Rook, PieceColor.Red, 40);
        int blackMirror = PieceSquareTables.GetScore(PieceType.Rook, PieceColor.Black, 89 - 40);

        Assert.Equal(redCenter, blackMirror);
    }

    // --- Evaluator 測試 ---

    [Fact]
    public void Evaluator_MissingAdvisor_PenalizesKingSafety()
    {
        var fullDefense = new Board("4k4/4a4/4b4/9/9/9/9/4B4/4A4/4K4 w - - 0 1");
        var missingAdvisor = new Board("4k4/4a4/4b4/9/9/9/9/4B4/9/4K4 w - - 0 1");

        var evaluator = new HandcraftedEvaluator();
        int fullScore = evaluator.Evaluate(fullDefense);
        int missingScore = evaluator.Evaluate(missingAdvisor);

        Assert.True(fullScore > missingScore);
    }

    [Fact]
    public void Evaluator_Evaluate_ShouldNotMutateBoardState()
    {
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        var evaluator = new HandcraftedEvaluator();
        var initialTurn = board.Turn;
        var initialPieces = new Piece[90];
        var initialKey = board.ZobristKey;

        for (int i = 0; i < 90; i++)
        {
            initialPieces[i] = board.GetPiece(i);
        }

        int first = evaluator.Evaluate(board);
        int second = evaluator.Evaluate(board);

        Assert.Equal(first, second);
        Assert.Equal(initialTurn, board.Turn);
        Assert.Equal(initialKey, board.ZobristKey);

        for (int i = 0; i < 90; i++)
        {
            Assert.Equal(initialPieces[i], board.GetPiece(i));
        }
    }

    [Fact]
    public async Task Search_StableResult_WithSameSettingsAndBoard()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000, ThreadCount = 1 };

        var first = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);
        var second = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);

        Assert.Equal(first.BestMove, second.BestMove);
    }

    // --- Null-Move / 棋盤測試 ---

    [Fact]
    public void Board_MakeNullMove_SwitchesTurnAndZobrist()
    {
        var board = new Board(InitialFen);
        var turnBefore = board.Turn;
        var keyBefore = board.ZobristKey;

        board.MakeNullMove();

        Assert.NotEqual(turnBefore, board.Turn);
        Assert.NotEqual(keyBefore, board.ZobristKey);

        board.UnmakeNullMove();

        Assert.Equal(turnBefore, board.Turn);
        Assert.Equal(keyBefore, board.ZobristKey);
    }

    // --- Check Extension（檢查延伸）測試 ---

    [Fact]
    public async Task Search_CheckPosition_ShouldFindBestResponse()
    {
        var board = new Board("3ak4/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 5000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.False(result.BestMove.IsNull);
    }

    // --- MVV-LVA 排序測試 ---

    [Fact]
    public async Task Search_ShouldPreferCapturingHighValuePiece()
    {
        // 紅方車可吃黑車或黑兵，應優先選擇吃車
        var board = new Board("4k4/9/9/9/4r4/3RP4/9/9/9/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 1, TimeLimitMs = 3000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.False(result.BestMove.IsNull);
        var captured = board.GetPiece(result.BestMove.To);
        if (!captured.IsNone)
        {
            Assert.Equal(PieceType.Rook, captured.Type);
        }
    }
}
