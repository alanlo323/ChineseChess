using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Book;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

public class OpeningBookEngineDecoratorTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── Fake Inner Engine ─────────────────────────────────────────────

    private class FakeInnerEngine : IAiEngine
    {
        public int SearchCallCount;
        private readonly Move fixedMove;

        public FakeInnerEngine(Move fixedMove)
        {
            this.fixedMove = fixedMove;
        }

        public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
        {
            Interlocked.Increment(ref SearchCallCount);
            return Task.FromResult(new SearchResult { BestMove = fixedMove, Score = 100, Depth = 5, Nodes = 500 });
        }

        public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(IBoard board, IEnumerable<Move> moves, int depth, CancellationToken ct = default, IProgress<string>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);

        public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public TTStatistics GetTTStatistics() => new TTStatistics();
        public IAiEngine CloneWithCopiedTT() => new FakeInnerEngine(fixedMove);
        public IAiEngine CloneWithEmptyTT() => new FakeInnerEngine(fixedMove);
        public void MergeTranspositionTableFrom(IAiEngine other) { }
        public IEnumerable<TTEntry> EnumerateTTEntries() => [];
        public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) => null;
        public Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(IBoard board, SearchSettings settings, int pvCount, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);
    }

    private static (OpeningBookEngineDecorator decorator, FakeInnerEngine innerEngine) BuildDecorator(
        IOpeningBook book,
        Move innerMove,
        OpeningBookSettings? settings = null)
    {
        var inner = new FakeInnerEngine(innerMove);
        var bookSettings = settings ?? new OpeningBookSettings { IsEnabled = true, MaxPly = 20 };
        var decorator = new OpeningBookEngineDecorator(inner, book, bookSettings);
        return (decorator, inner);
    }

    // ─── 測試 1：開局庫命中時跳過 inner engine ────────────────────────

    [Fact]
    public async Task WhenBookHits_SkipsInnerEngine()
    {
        var board = new Board(InitialFen);
        var bookMove = new Move(64, 67);  // 炮二平五（合法紅方走法）
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(bookMove, 100)]);

        var (decorator, inner) = BuildDecorator(book, new Move(82, 65));
        var settings = new SearchSettings { AllowOpeningBook = true };

        var result = await decorator.SearchAsync(board, settings);

        Assert.Equal(0, inner.SearchCallCount);
        Assert.True(result.IsFromOpeningBook);
        Assert.Equal(bookMove, result.BestMove);
    }

    // ─── 測試 2：開局庫未命中時 fallback 到 inner engine ─────────────

    [Fact]
    public async Task WhenBookMisses_FallsBackToInnerEngine()
    {
        var board = new Board(InitialFen);
        var emptyBook = new OpeningBook();
        var innerMove = new Move(64, 67);

        var (decorator, inner) = BuildDecorator(emptyBook, innerMove);
        var settings = new SearchSettings { AllowOpeningBook = true };

        var result = await decorator.SearchAsync(board, settings);

        Assert.Equal(1, inner.SearchCallCount);
        Assert.False(result.IsFromOpeningBook);
        Assert.Equal(innerMove, result.BestMove);
    }

    // ─── 測試 3：AllowOpeningBook=false 時跳過開局庫（hint 模式）───────

    [Fact]
    public async Task WhenAllowOpeningBookFalse_SkipsBook()
    {
        var board = new Board(InitialFen);
        var bookMove = new Move(64, 67);
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(bookMove, 100)]);

        var innerMove = new Move(82, 65);
        var (decorator, inner) = BuildDecorator(book, innerMove);
        var settings = new SearchSettings { AllowOpeningBook = false };

        var result = await decorator.SearchAsync(board, settings);

        // hint 模式不查書庫，應直接用 inner engine
        Assert.Equal(1, inner.SearchCallCount);
        Assert.False(result.IsFromOpeningBook);
        Assert.Equal(innerMove, result.BestMove);
    }

    // ─── 測試 4：IsEnabled=false 時跳過開局庫 ────────────────────────

    [Fact]
    public async Task WhenIsEnabledFalse_SkipsBook()
    {
        var board = new Board(InitialFen);
        var bookMove = new Move(64, 67);
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(bookMove, 100)]);

        var innerMove = new Move(82, 65);
        var bookSettings = new OpeningBookSettings { IsEnabled = false, MaxPly = 20 };
        var (decorator, inner) = BuildDecorator(book, innerMove, bookSettings);
        var settings = new SearchSettings { AllowOpeningBook = true };

        var result = await decorator.SearchAsync(board, settings);

        Assert.Equal(1, inner.SearchCallCount);
        Assert.False(result.IsFromOpeningBook);
    }

    // ─── 測試 5：MoveCount >= MaxPly 時跳過開局庫 ────────────────────

    [Fact]
    public async Task WhenMoveCountExceedsMaxPly_SkipsBook()
    {
        var board = new Board(InitialFen);
        board.MakeMove(new Move(64, 67));  // MoveCount = 1

        var bookMove = new Move(7, 24);
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(bookMove, 100)]);

        var innerMove = new Move(1, 20);
        // MaxPly = 1 → MoveCount(1) >= MaxPly(1)，跳過
        var bookSettings = new OpeningBookSettings { IsEnabled = true, MaxPly = 1 };
        var (decorator, inner) = BuildDecorator(book, innerMove, bookSettings);
        var settings = new SearchSettings { AllowOpeningBook = true };

        var result = await decorator.SearchAsync(board, settings);

        Assert.Equal(1, inner.SearchCallCount);
        Assert.False(result.IsFromOpeningBook);
    }

    // ─── 測試 6：Zobrist 碰撞（非法走法）時 fallback ──────────────────

    [Fact]
    public async Task WhenBookMoveIsIllegal_FallsBackToInnerEngine()
    {
        var board = new Board(InitialFen);
        // 手動設定一個非法走法的 hash 碰撞情境
        var illegalMove = new Move(0, 89);  // 棋盤角到角，非法走法
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(illegalMove, 100)]);

        var innerMove = new Move(64, 67);
        var (decorator, inner) = BuildDecorator(book, innerMove);
        var settings = new SearchSettings { AllowOpeningBook = true };

        var result = await decorator.SearchAsync(board, settings);

        // 非法走法應 fallback 到 inner engine
        Assert.Equal(1, inner.SearchCallCount);
        Assert.False(result.IsFromOpeningBook);
        Assert.Equal(innerMove, result.BestMove);
    }

    // ─── 測試 7：CloneWithCopiedTT 後的 Decorator 仍保有開局庫能力 ───

    [Fact]
    public async Task CloneWithCopiedTT_PreservesOpeningBookBehavior()
    {
        var board = new Board(InitialFen);
        var bookMove = new Move(64, 67);
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(bookMove, 100)]);

        var (decorator, _) = BuildDecorator(book, new Move(82, 65));
        var cloned = decorator.CloneWithCopiedTT();
        var settings = new SearchSettings { AllowOpeningBook = true };

        var result = await cloned.SearchAsync(board, settings);

        Assert.True(result.IsFromOpeningBook);
        Assert.Equal(bookMove, result.BestMove);
    }

    // ─── 測試 8：CloneWithEmptyTT 後的 Decorator 仍保有開局庫能力 ────

    [Fact]
    public async Task CloneWithEmptyTT_PreservesOpeningBookBehavior()
    {
        var board = new Board(InitialFen);
        var bookMove = new Move(64, 67);
        var book = new OpeningBook(useRandomSelection: false);
        book.SetEntry(board.ZobristKey, [(bookMove, 100)]);

        var (decorator, _) = BuildDecorator(book, new Move(82, 65));
        var cloned = decorator.CloneWithEmptyTT();
        var settings = new SearchSettings { AllowOpeningBook = true };

        var result = await cloned.SearchAsync(board, settings);

        Assert.True(result.IsFromOpeningBook);
        Assert.Equal(bookMove, result.BestMove);
    }

    // ─── 測試 9：MergeTranspositionTableFrom 穿透 inner 的 spy 測試 ───

    /// <summary>spy inner engine：記錄 MergeTranspositionTableFrom 收到的 other 參考。</summary>
    private class SpyInnerEngine : IAiEngine
    {
        public IAiEngine? MergeCalledWithOther;

        public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
            => Task.FromResult(new SearchResult { BestMove = new Move(64, 67), Score = 0, Depth = 1, Nodes = 1 });

        public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(IBoard board, IEnumerable<Move> moves, int depth, CancellationToken ct = default, IProgress<string>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);

        public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public TTStatistics GetTTStatistics() => new TTStatistics();
        public IAiEngine CloneWithCopiedTT() => this;
        public IAiEngine CloneWithEmptyTT() => this;
        public void MergeTranspositionTableFrom(IAiEngine other) { MergeCalledWithOther = other; }
        public IEnumerable<TTEntry> EnumerateTTEntries() => [];
        public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) => null;
        public Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(IBoard board, SearchSettings settings, int pvCount, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);
    }

    [Fact]
    public void MergeTranspositionTableFrom_WithDecorator_PenetratesInner()
    {
        var book = new OpeningBook();
        var spyA = new SpyInnerEngine();
        var spyB = new SpyInnerEngine();
        var bookSettings = new OpeningBookSettings { IsEnabled = true, MaxPly = 20 };

        var decoratorA = new OpeningBookEngineDecorator(spyA, book, bookSettings);
        var decoratorB = new OpeningBookEngineDecorator(spyB, book, bookSettings);

        // decoratorA.MergeFrom(decoratorB) → 應呼叫 spyA.MergeFrom(spyB.inner)
        decoratorA.MergeTranspositionTableFrom(decoratorB);

        // spy 驗證：inner 收到的 other 是 spyB（非外層 Decorator）
        Assert.Same(spyB, spyA.MergeCalledWithOther);
    }

    [Fact]
    public void MergeTranspositionTableFrom_WithSelf_DoesNothing()
    {
        var book = new OpeningBook();
        var spy = new SpyInnerEngine();
        var bookSettings = new OpeningBookSettings { IsEnabled = true, MaxPly = 20 };
        var decorator = new OpeningBookEngineDecorator(spy, book, bookSettings);

        // 自我合併不應拋例外，也不應呼叫 inner
        decorator.MergeTranspositionTableFrom(decorator);

        Assert.Null(spy.MergeCalledWithOther);
    }
}
