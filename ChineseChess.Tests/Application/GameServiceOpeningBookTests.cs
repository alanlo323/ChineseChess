using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Book;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

public class GameServiceOpeningBookTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── Fake AI Engine（記錄呼叫次數，回傳指定走法）───────────────────

    private class FakeAiEngine : IAiEngine
    {
        public int SearchCallCount;
        private readonly Move fixedMove;

        /// <param name="fixedMove">SearchAsync 固定回傳的走法。</param>
        public FakeAiEngine(Move fixedMove)
        {
            this.fixedMove = fixedMove;
        }

        public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
        {
            Interlocked.Increment(ref SearchCallCount);
            return Task.FromResult(new SearchResult { BestMove = fixedMove, Score = 0, Depth = 1, Nodes = 1 });
        }

        public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(IBoard board, IEnumerable<Move> moves, int depth, CancellationToken ct = default, IProgress<string>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);

        public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public TTStatistics GetTTStatistics() => new TTStatistics();
        public IAiEngine CloneWithCopiedTT() => new FakeAiEngine(fixedMove);
        public IAiEngine CloneWithEmptyTT() => new FakeAiEngine(fixedMove);
        public void MergeTranspositionTableFrom(IAiEngine other) { }
        public IEnumerable<TTEntry> EnumerateTTEntries() => [];
        public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) => null;
        public Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(IBoard board, SearchSettings settings, int pvCount, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);
    }

    // ─── 測試 1：開局庫命中時跳過 AI 搜尋（PlayerVsAi 模式）────────────

    [Fact]
    public async Task OpeningBookHit_PlayerVsAi_SkipsAiSearch()
    {
        // 玩家（紅方）走兵一進一後，輪到黑方（AI）。
        // 書庫記錄黑方應走馬8進7，AI fake 本來要選馬2進3。
        var boardAfterPlayer = new Board(InitialFen);
        boardAfterPlayer.MakeMove(new Move(54, 45));  // 玩家：兵一進一（紅方走法）

        var bookMoveBlack = new Move(7, 24);   // 黑方馬8進7（書庫手）
        var aiDefaultMove  = new Move(1, 20);  // 黑方馬2進3（AI fake 回傳）

        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(boardAfterPlayer.ZobristKey, [(bookMoveBlack, 100)]);

        var fakeEngine = new FakeAiEngine(aiDefaultMove);
        var settings = new OpeningBookSettings { IsEnabled = true, MaxPly = 20 };
        var gameService = new GameService(fakeEngine, null, book, settings);
        gameService.SetDifficulty(2, 3000, 1);

        await gameService.StartGameAsync(GameMode.PlayerVsAi);
        await gameService.HumanMoveAsync(new Move(54, 45));  // 玩家走兵一進一

        // 書庫命中，AI 搜尋不應被呼叫
        Assert.Equal(0, fakeEngine.SearchCallCount);
        // AI 應走馬8進7（書庫手），而非馬2進3
        Assert.Equal(bookMoveBlack, gameService.LastMove);
    }

    // ─── 測試 2：開局庫未命中時 fallback 到 AI ──────────────────────────

    [Fact]
    public async Task OpeningBookMiss_AiVsAi_UsesAiSearch()
    {
        // 書庫是空的，找不到任何局面。AI fake 回傳炮二平五（合法紅方走法）。
        var emptyBook = new OpeningBook();
        var fakeEngine = new FakeAiEngine(new Move(64, 67));  // 炮二平五（合法）
        var settings = new OpeningBookSettings { IsEnabled = true, MaxPly = 20 };
        var gameService = new GameService(fakeEngine, null, emptyBook, settings);
        gameService.SetDifficulty(2, 3000, 1);

        await gameService.StartGameAsync(GameMode.AiVsAi);

        // 書庫未命中，AI 應被呼叫
        Assert.True(fakeEngine.SearchCallCount >= 1);
    }

    // ─── 測試 3：IsEnabled=false 時停用開局庫 ────────────────────────────

    [Fact]
    public async Task OpeningBookDisabled_AiVsAi_UsesAiSearch()
    {
        var board = new Board(InitialFen);
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(new Move(64, 67), 100)]);  // 紅方炮二平五

        var fakeEngine = new FakeAiEngine(new Move(64, 67));  // 炮二平五（合法）
        var settings = new OpeningBookSettings { IsEnabled = false, MaxPly = 20 };
        var gameService = new GameService(fakeEngine, null, book, settings);
        gameService.SetDifficulty(2, 3000, 1);

        await gameService.StartGameAsync(GameMode.AiVsAi);

        // 書庫已停用，AI 應被呼叫
        Assert.True(fakeEngine.SearchCallCount >= 1);
    }

    // ─── 測試 4：超過 MaxPly 後停用開局庫 ────────────────────────────────

    [Fact]
    public async Task OpeningBookMaxPlyExceeded_PlayerVsAi_UsesAiSearch()
    {
        // MaxPly = 0 → board.MoveCount（=1 after player move） >= MaxPly，書庫不查
        var boardAfterPlayer = new Board(InitialFen);
        boardAfterPlayer.MakeMove(new Move(54, 45));
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(boardAfterPlayer.ZobristKey, [(new Move(7, 24), 100)]);  // 黑方馬8進7

        var fakeEngine = new FakeAiEngine(new Move(1, 20));  // 黑方馬2進3（合法）
        var settings = new OpeningBookSettings { IsEnabled = true, MaxPly = 0 };
        var gameService = new GameService(fakeEngine, null, book, settings);
        gameService.SetDifficulty(2, 3000, 1);

        await gameService.StartGameAsync(GameMode.PlayerVsAi);
        await gameService.HumanMoveAsync(new Move(54, 45));

        // board.MoveCount = 1 >= MaxPly = 0，書庫跳過，AI 應被呼叫
        Assert.True(fakeEngine.SearchCallCount >= 1);
    }

    // ─── 測試 5：IsOpeningBookLoaded 與 OpeningBookEntryCount ────────────

    [Fact]
    public void IsOpeningBookLoaded_WhenBookIsEmpty_ReturnsFalse()
    {
        var emptyBook = new OpeningBook();
        var fakeEngine = new FakeAiEngine(new Move(64, 67));
        var gameService = new GameService(fakeEngine, null, emptyBook);

        Assert.False(gameService.IsOpeningBookLoaded);
        Assert.Equal(0, gameService.OpeningBookEntryCount);
    }

    [Fact]
    public void IsOpeningBookLoaded_WhenBookHasEntries_ReturnsTrue()
    {
        var book = new OpeningBook();
        book.SetEntry(1UL, [(new Move(1, 2), 5)]);
        var fakeEngine = new FakeAiEngine(new Move(64, 67));
        var gameService = new GameService(fakeEngine, null, book);

        Assert.True(gameService.IsOpeningBookLoaded);
        Assert.Equal(1, gameService.OpeningBookEntryCount);
    }

    // ─── 測試 6：開局庫命中時 ThinkingProgress 含"開局庫"字樣 ─────────

    [Fact]
    public async Task OpeningBookHit_ThinkingProgress_ContainsBookLabel()
    {
        // AiVsAi：初始局面有書庫記錄（紅方炮二平五），AI fake 無機會被呼叫
        var board = new Board(InitialFen);
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(new Move(64, 67), 100)]);  // 紅方炮二平五

        var fakeEngine = new FakeAiEngine(new Move(82, 65));  // AI 本來選馬二進三
        var settings = new OpeningBookSettings { IsEnabled = true, MaxPly = 20 };
        var gameService = new GameService(fakeEngine, null, book, settings);
        gameService.SetDifficulty(2, 3000, 1);

        var messages = new List<string>();
        gameService.ThinkingProgress += msg => messages.Add(msg);

        await gameService.StartGameAsync(GameMode.AiVsAi);

        // 至少有一條訊息含「開局庫」
        Assert.Contains(messages, m => m.Contains("開局庫"));
    }
}
