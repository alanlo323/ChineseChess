using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Book;

/// <summary>
/// 開局庫引擎裝飾器：在內建引擎外層加上開局庫查詢。
/// 命中時直接回傳書庫走法，略過 inner engine；
/// 未命中或 AllowOpeningBook=false 時 delegate 給 inner engine。
/// 外部引擎（ExternalEngineAdapter）不被此 Decorator 包裝，因此完全不受影響。
/// </summary>
public class OpeningBookEngineDecorator : IAiEngine
{
    private readonly IAiEngine inner;
    private readonly IOpeningBook openingBook;
    private readonly OpeningBookSettings settings;

    public OpeningBookEngineDecorator(IAiEngine inner, IOpeningBook openingBook, OpeningBookSettings settings)
    {
        this.inner = inner;
        this.openingBook = openingBook;
        this.settings = settings;
    }

    public string EngineLabel => inner.EngineLabel;

    public async Task<SearchResult> SearchAsync(
        IBoard board,
        SearchSettings searchSettings,
        CancellationToken ct = default,
        IProgress<SearchProgress>? progress = null)
    {
        // AllowOpeningBook=false 時（如 hint 模式）跳過，直接交給 inner engine
        if (searchSettings.AllowOpeningBook)
        {
            var bookResult = TryProbeOpeningBook(board);
            if (bookResult != null) return bookResult;
        }

        return await inner.SearchAsync(board, searchSettings, ct, progress);
    }

    /// <summary>
    /// 查詢開局庫。命中且走法合法時回傳 SearchResult；否則回傳 null。
    /// </summary>
    private SearchResult? TryProbeOpeningBook(IBoard board)
    {
        if (!settings.IsEnabled) return null;
        if (board.MoveCount >= settings.MaxPly) return null;
        if (!openingBook.TryProbe(board.ZobristKey, out var bookMove)) return null;

        // 合法性驗證（防護 Zobrist hash collision 極罕見情形）
        if (!board.GenerateLegalMoves().Any(m => m == bookMove)) return null;

        return new SearchResult
        {
            BestMove = bookMove,
            IsFromOpeningBook = true,
            Score = 0,
            Depth = 0,
            Nodes = 0,
            PvLine = string.Empty
        };
    }

    // ─── 以下所有方法均 delegate 給 inner engine ────────────────────────

    public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(
        IBoard board, IEnumerable<Move> moves, int depth,
        CancellationToken ct = default, IProgress<string>? progress = null)
        => inner.EvaluateMovesAsync(board, moves, depth, ct, progress);

    public Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(
        IBoard board, SearchSettings searchSettings, int pvCount,
        CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
        => inner.SearchMultiPvAsync(board, searchSettings, pvCount, ct, progress);

    public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default)
        => inner.ExportTranspositionTableAsync(output, asJson, ct);

    public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default)
        => inner.ImportTranspositionTableAsync(input, asJson, ct);

    public TTStatistics GetTTStatistics()
        => inner.GetTTStatistics();

    /// <summary>Clone 後的 Decorator 同樣保有開局庫能力。</summary>
    public IAiEngine CloneWithCopiedTT()
        => new OpeningBookEngineDecorator(inner.CloneWithCopiedTT(), openingBook, settings);

    /// <summary>Clone 後的 Decorator 同樣保有開局庫能力。</summary>
    public IAiEngine CloneWithEmptyTT()
        => new OpeningBookEngineDecorator(inner.CloneWithEmptyTT(), openingBook, settings);

    public void MergeTranspositionTableFrom(IAiEngine other)
    {
        if (ReferenceEquals(this, other)) return;
        // 若 other 也是 Decorator，穿透至 inner 以避免 Decorator 包裝干擾
        if (other is OpeningBookEngineDecorator otherDecorator)
            inner.MergeTranspositionTableFrom(otherDecorator.inner);
        else
            inner.MergeTranspositionTableFrom(other);
    }

    public bool IsOpeningBookLoaded => openingBook.IsLoaded;
    public int OpeningBookEntryCount => openingBook.EntryCount;

    public IEnumerable<TTEntry> EnumerateTTEntries()
        => inner.EnumerateTTEntries();

    public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6)
        => inner.ExploreTTTree(board, maxDepth);
}
