using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// 殘局庫必勝著法整合測試。
/// 驗證：AI 走棋 / 提示 / 智能提示在殘局庫有 Win 結論時直接使用 ETB 最佳著法。
/// </summary>
public class GameServiceTablebaseTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Fake 依賴
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>可控 AI 引擎：記錄呼叫次數，回傳固定走法。</summary>
    private class FakeAiEngine : IAiEngine
    {
        public int SearchCallCount;
        private readonly Move fixedMove;

        public FakeAiEngine(Move fixedMove) => this.fixedMove = fixedMove;

        public Task<SearchResult> SearchAsync(
            IBoard board, SearchSettings settings,
            CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
        {
            Interlocked.Increment(ref SearchCallCount);
            return Task.FromResult(new SearchResult { BestMove = fixedMove, Score = 50, Depth = 3 });
        }

        public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(
            IBoard board, IEnumerable<Move> moves, int depth,
            CancellationToken ct = default, IProgress<string>? progress = null)
        {
            // 每個傳入的走法都回傳一個評估（分數 0，IsBest = false）
            var evals = moves.Select(m => new MoveEvaluation { Move = m, Score = 0 })
                             .ToList();
            return Task.FromResult<IReadOnlyList<MoveEvaluation>>(evals);
        }

        public Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(
            IBoard board, SearchSettings settings, int pvCount,
            CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);

        public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public TTStatistics GetTTStatistics() => new TTStatistics();
        public IAiEngine CloneWithCopiedTT() => new FakeAiEngine(fixedMove);
        public IAiEngine CloneWithEmptyTT() => new FakeAiEngine(fixedMove);
        public void MergeTranspositionTableFrom(IAiEngine other) { }
        public IEnumerable<TTEntry> EnumerateTTEntries() => [];
        public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) => null;
    }

    /// <summary>可控殘局庫服務：回傳指定的 Win 結論與最佳著法。</summary>
    private class FakeTablebaseService : ITablebaseService
    {
        public bool HasTablebase { get; set; } = true;
        public PieceConfiguration? CurrentConfiguration => null;
        public bool IsGenerating => false;
        public int TotalPositions => 0;
        public int WinPositions => 0;
        public int LossPositions => 0;
        public int DrawPositions => 0;
        public bool HasBoardData => false;

        /// <summary>Query 回傳的結論（預設 Win(3)）。</summary>
        public TablebaseEntry Entry { get; set; } = new TablebaseEntry(TablebaseResult.Win, 3);

        /// <summary>GetBestMove 回傳的著法（null 表示無著法）。</summary>
        public Move? BestMoveResult { get; set; }

        public TablebaseEntry Query(IBoard board) => Entry;
        public Move? GetBestMove(IBoard board) => BestMoveResult;

        public Task GenerateAsync(PieceConfiguration config, IProgress<TablebaseGenerationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task GenerateFromBoardAsync(IBoard board, IProgress<TablebaseGenerationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Clear() { }
        public Task ExportToFileAsync(string filePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void SyncToTranspositionTable(IAiEngine engine) { }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 輔助：初始局面 FEN（紅先）
    // ──────────────────────────────────────────────────────────────────────

    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    /// <summary>建立 GameService，注入 FakeAiEngine + FakeTablebaseService。</summary>
    private static (GameService service, FakeAiEngine fakeEngine, FakeTablebaseService fakeEtb)
        BuildService(Move aiMove, Move etbMove, bool etbHasTablebase = true)
    {
        var fakeEngine = new FakeAiEngine(aiMove);
        var fakeEtb = new FakeTablebaseService
        {
            HasTablebase = etbHasTablebase,
            Entry = etbHasTablebase
                ? new TablebaseEntry(TablebaseResult.Win, 3)
                : TablebaseEntry.Unknown,
            BestMoveResult = etbMove
        };
        var service = new GameService(fakeEngine, tablebaseService: fakeEtb);
        service.SetDifficulty(depth: 3, timeMs: 5000, threadCount: 1);
        return (service, fakeEngine, fakeEtb);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 測試群組 1：AI 走棋模式（applyBestMove = true）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TablebaseWin_AiVsAi_SkipsAiSearchAndUsesEtbMove()
    {
        // 殘局庫 Win + GetBestMove = 炮二平五（64→67）
        // FakeAi 的 fixedMove = 馬二進三（82→65）
        // 期望：AI 不呼叫 SearchAsync，棋盤走了炮二平五
        var etbMove = new Move(64, 67);   // 炮二平五
        var aiMove  = new Move(82, 65);   // 馬二進三（不應被選）
        var (service, fakeEngine, _) = BuildService(aiMove, etbMove);

        await service.StartGameAsync(GameMode.AiVsAi);

        // 等待第一步 AI 完成
        var waited = 0;
        while (service.IsThinking && waited < 300)
        {
            await Task.Delay(10);
            waited++;
        }

        Assert.Equal(0, fakeEngine.SearchCallCount);
        Assert.Equal(etbMove, service.LastMove);
    }

    [Fact]
    public async Task TablebaseWin_PlayerVsAi_SkipsAiSearch()
    {
        // PlayerVsAi：玩家走兵一進一後，AI 應用殘局庫著法
        var etbMove = new Move(64, 67);   // 炮二平五（黑方視角下的 ETB 著法）
        var aiMove  = new Move(82, 65);
        var (service, fakeEngine, _) = BuildService(aiMove, etbMove);

        await service.StartGameAsync(GameMode.PlayerVsAi);
        await service.HumanMoveAsync(new Move(54, 45));  // 玩家：兵一進一

        var waited = 0;
        while (service.IsThinking && waited < 300)
        {
            await Task.Delay(10);
            waited++;
        }

        Assert.Equal(0, fakeEngine.SearchCallCount);
    }

    [Fact]
    public async Task TablebaseNotLoaded_AiVsAi_FallsBackToAiSearch()
    {
        // 殘局庫未載入 → 應走 AI 搜尋
        var aiMove  = new Move(64, 67);
        var etbMove = new Move(82, 65);
        var (service, fakeEngine, _) = BuildService(aiMove, etbMove, etbHasTablebase: false);

        await service.StartGameAsync(GameMode.AiVsAi);

        var waited = 0;
        while (service.IsThinking && waited < 300)
        {
            await Task.Delay(10);
            waited++;
        }

        Assert.True(fakeEngine.SearchCallCount >= 1, "殘局庫未載入時應呼叫 AI 搜尋");
    }

    [Fact]
    public async Task TablebaseWin_GameMessage_ContainsTablebaseLabel()
    {
        // 走棋訊息應含「殘局庫」字樣
        var etbMove = new Move(64, 67);
        var aiMove  = new Move(82, 65);
        var (service, _, _) = BuildService(aiMove, etbMove);

        var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
        service.GameMessage += msg => messages.Add(msg);

        await service.StartGameAsync(GameMode.AiVsAi);

        var waited = 0;
        while (service.IsThinking && waited < 300)
        {
            await Task.Delay(10);
            waited++;
        }

        Assert.Contains(messages, m => m.Contains("殘局庫"));
    }

    [Fact]
    public async Task TablebaseWin_ThinkingProgress_ContainsTablebaseLabel()
    {
        // ThinkingProgress 訊息應含殘局庫相關字樣
        var etbMove = new Move(64, 67);
        var aiMove  = new Move(82, 65);
        var (service, _, _) = BuildService(aiMove, etbMove);

        var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
        service.ThinkingProgress += msg => messages.Add(msg);

        await service.StartGameAsync(GameMode.AiVsAi);

        var waited = 0;
        while (service.IsThinking && waited < 300)
        {
            await Task.Delay(10);
            waited++;
        }

        Assert.Contains(messages, m => m.Contains("殘局庫") || m.Contains("必勝"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // 測試群組 2：提示模式（GetHintAsync）
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TablebaseWin_GetHintAsync_ReturnsEtbMove()
    {
        // GetHintAsync 應直接回傳 ETB 最佳著法
        var etbMove = new Move(64, 67);
        var aiMove  = new Move(82, 65);
        var (service, fakeEngine, _) = BuildService(aiMove, etbMove);

        await service.StartGameAsync(GameMode.PlayerVsAi);
        var result = await service.GetHintAsync();

        Assert.Equal(etbMove, result.BestMove);
    }

    [Fact]
    public async Task TablebaseWin_GetHintAsync_IsFromTablebaseTrue()
    {
        // 殘局庫命中的 SearchResult 應標記 IsFromTablebase = true
        var etbMove = new Move(64, 67);
        var aiMove  = new Move(82, 65);
        var (service, _, _) = BuildService(aiMove, etbMove);

        await service.StartGameAsync(GameMode.PlayerVsAi);
        var result = await service.GetHintAsync();

        Assert.True(result.IsFromTablebase, "殘局庫命中應標記 IsFromTablebase");
    }

    [Fact]
    public async Task TablebaseWin_GetHintAsync_SkipsAiSearch()
    {
        // 殘局庫命中 → GetHintAsync 不應呼叫 AI 搜尋
        var etbMove = new Move(64, 67);
        var aiMove  = new Move(82, 65);
        var (service, fakeEngine, _) = BuildService(aiMove, etbMove);

        await service.StartGameAsync(GameMode.PlayerVsAi);
        await service.GetHintAsync();

        Assert.Equal(0, fakeEngine.SearchCallCount);
    }

    [Fact]
    public async Task TablebaseWin_HintReady_FiredWithEtbResult()
    {
        // HintReady 事件應收到 IsFromTablebase = true 且 BestMove = ETB 著法
        var etbMove = new Move(64, 67);
        var aiMove  = new Move(82, 65);
        var (service, _, _) = BuildService(aiMove, etbMove);

        SearchResult? capturedHint = null;
        service.HintReady += r => capturedHint = r;

        await service.StartGameAsync(GameMode.PlayerVsAi);
        await service.GetHintAsync();

        Assert.NotNull(capturedHint);
        Assert.Equal(etbMove, capturedHint!.BestMove);
        Assert.True(capturedHint.IsFromTablebase);
    }

    [Fact]
    public async Task TablebaseWin_GetHintAsync_ScoreReflectsDepth()
    {
        // Win(Depth=3) 的分數應為 20000 - 3 = 19997
        var etbMove = new Move(64, 67);
        var aiMove  = new Move(82, 65);
        var (service, _, fakeEtb) = BuildService(aiMove, etbMove);
        fakeEtb.Entry = new TablebaseEntry(TablebaseResult.Win, 3);

        await service.StartGameAsync(GameMode.PlayerVsAi);
        var result = await service.GetHintAsync();

        Assert.Equal(20000 - 3, result.Score);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 測試群組 3：智能提示（RequestSmartHintAsync）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 建立智能提示測試用 GameService（紅炮 index 64，取第一個合法目標作為 ETB 著法）。
    /// </summary>
    private static (GameService service, Move etbMove) BuildSmartHintService(bool etbLoaded = true)
    {
        var board = new Board(InitialFen);
        var cannonMoves = board.GenerateLegalMoves()
                               .Where(m => m.From == 64)
                               .ToList();
        var etbMove = cannonMoves[0];

        var fakeEngine = new FakeAiEngine(etbMove);
        var fakeEtb = new FakeTablebaseService
        {
            HasTablebase = etbLoaded,
            Entry = etbLoaded
                ? new TablebaseEntry(TablebaseResult.Win, 3)
                : TablebaseEntry.Unknown,
            BestMoveResult = etbMove
        };
        var service = new GameService(fakeEngine, tablebaseService: fakeEtb);
        service.SetDifficulty(2, 3000, 1);
        service.IsSmartHintEnabled = true;
        return (service, etbMove);
    }

    [Fact]
    public async Task TablebaseWin_SmartHint_MarksEtbMoveAsFromTablebase()
    {
        // 期望：SmartHintReady 中 ETB 最佳著法有 IsFromTablebase = true
        var (service, etbMove) = BuildSmartHintService();
        await service.StartGameAsync(GameMode.PlayerVsAi);

        IReadOnlyList<MoveEvaluation>? capturedEvals = null;
        service.SmartHintReady += evals => capturedEvals = evals;
        await service.RequestSmartHintAsync(64);

        Assert.NotNull(capturedEvals);
        var etbEval = capturedEvals!.FirstOrDefault(e => e.Move == etbMove);
        Assert.NotNull(etbEval);
        Assert.True(etbEval!.IsFromTablebase, "ETB 最佳著法應標記 IsFromTablebase");
    }

    [Fact]
    public async Task TablebaseWin_SmartHint_EtbMoveMarkedAsBest()
    {
        // 期望：SmartHintReady 中 ETB 最佳著法有 IsBest = true
        var (service, etbMove) = BuildSmartHintService();
        await service.StartGameAsync(GameMode.PlayerVsAi);

        IReadOnlyList<MoveEvaluation>? capturedEvals = null;
        service.SmartHintReady += evals => capturedEvals = evals;
        await service.RequestSmartHintAsync(64);

        Assert.NotNull(capturedEvals);
        var etbEval = capturedEvals!.FirstOrDefault(e => e.Move == etbMove);
        Assert.NotNull(etbEval);
        Assert.True(etbEval!.IsBest, "ETB 最佳著法應標記 IsBest");
    }

    [Fact]
    public async Task TablebaseNotLoaded_SmartHint_NoIsFromTablebaseMarked()
    {
        // 殘局庫未載入 → SmartHintReady 中不應有任何 IsFromTablebase = true
        var (service, _) = BuildSmartHintService(etbLoaded: false);
        await service.StartGameAsync(GameMode.PlayerVsAi);

        IReadOnlyList<MoveEvaluation>? capturedEvals = null;
        service.SmartHintReady += evals => capturedEvals = evals;
        await service.RequestSmartHintAsync(64);

        Assert.NotNull(capturedEvals);
        Assert.DoesNotContain(capturedEvals!, e => e.IsFromTablebase);
    }

    [Fact]
    public async Task TablebaseWin_GetBestMoveReturnsNull_FallsBackToAiSearch()
    {
        // 殘局庫有 Win 結論但 GetBestMove 回傳 null → 應 fallthrough 到 AI 搜尋
        var aiMove = new Move(64, 67);
        var fakeEngine = new FakeAiEngine(aiMove);
        var fakeEtb = new FakeTablebaseService
        {
            HasTablebase = true,
            Entry = new TablebaseEntry(TablebaseResult.Win, 3),
            BestMoveResult = null   // ← GetBestMove 回傳 null
        };
        var service = new GameService(fakeEngine, tablebaseService: fakeEtb);
        service.SetDifficulty(2, 3000, 1);

        await service.StartGameAsync(GameMode.PlayerVsAi);
        var result = await service.GetHintAsync();

        // ETB 無法提供著法 → AI 搜尋應被呼叫，結果來自 AI
        Assert.Equal(1, fakeEngine.SearchCallCount);
        Assert.False(result.IsFromTablebase);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 測試群組 4：IsFromTablebase 屬性基本驗證
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void SearchResult_IsFromTablebase_DefaultIsFalse()
    {
        var result = new SearchResult();
        Assert.False(result.IsFromTablebase);
    }

    [Fact]
    public void MoveEvaluation_IsFromTablebase_DefaultIsFalse()
    {
        var eval = new MoveEvaluation();
        Assert.False(eval.IsFromTablebase);
    }
}
