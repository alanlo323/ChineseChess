using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Constants;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using System.Collections.Generic;

namespace ChineseChess.Infrastructure.Tablebase;

/// <summary>殘局庫服務實作。</summary>
public sealed class TablebaseService : ITablebaseService
{
    private readonly TablebaseStorage storage = new();
    private readonly Dictionary<ulong, Board> boardIndex = [];

    private PieceConfiguration? currentConfig;
    // 0 = 閒置，1 = 生成中（Interlocked 保護 check-then-set 原子性）
    private int isGeneratingFlag;

    // ── ITablebaseService ────────────────────────────────────────────────

    public bool HasTablebase  => storage.TotalPositions > 0;
    public bool HasBoardData  => boardIndex.Count > 0;
    public PieceConfiguration? CurrentConfiguration => currentConfig;
    public bool IsGenerating  => Volatile.Read(ref isGeneratingFlag) == 1;

    public int TotalPositions => storage.TotalPositions;
    public int WinPositions   => storage.WinCount;
    public int LossPositions  => storage.LossCount;
    public int DrawPositions  => storage.DrawCount;

    public async Task GenerateAsync(
        PieceConfiguration config,
        IProgress<TablebaseGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 原子性 check-then-set：確保不會有兩個呼叫同時進入生成流程
        if (Interlocked.CompareExchange(ref isGeneratingFlag, 1, 0) != 0)
            throw new InvalidOperationException("殘局庫生成中，請勿重複呼叫。");

        currentConfig = config;
        storage.Clear();
        boardIndex.Clear();

        try
        {
            await Task.Run(() =>
            {
                // 先列舉所有局面並建立 Board 索引
                var internalProgress = new Progress<(string phase, long done, long total)>(t =>
                {
                    var (phase, done, total) = t;
                    progress?.Report(new TablebaseGenerationProgress(
                        phase, done, total,
                        storage.WinCount, storage.LossCount, storage.DrawCount,
                        IsComplete: false));
                });

                // 列舉所有局面（同時建立 boardIndex 供後續匯出 FEN 使用）
                progress?.Report(new TablebaseGenerationProgress("列舉局面", 0, 1, 0, 0, 0, false));

                foreach (var board in PositionEnumerator.Enumerate(config))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    boardIndex[board.ZobristKey] = board;
                }

                long posCount = boardIndex.Count;
                progress?.Report(new TablebaseGenerationProgress("列舉局面", posCount, posCount, 0, 0, 0, false));

                // 執行倒推分析（傳入已建立的局面索引，避免重複窮舉）
                var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);
                analyzer.Analyze(boardIndex, internalProgress, cancellationToken);

            }, cancellationToken);

            progress?.Report(new TablebaseGenerationProgress(
                "完成", storage.TotalPositions, storage.TotalPositions,
                storage.WinCount, storage.LossCount, storage.DrawCount,
                IsComplete: true));
        }
        catch (OperationCanceledException)
        {
            storage.Clear();
            boardIndex.Clear();
            currentConfig = null;
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref isGeneratingFlag, 0);
        }
    }

    public void Clear()
    {
        storage.Clear();
        boardIndex.Clear();
        currentConfig = null;
    }

    public async Task GenerateFromBoardAsync(
        IBoard board,
        IProgress<TablebaseGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 驗證棋盤必須包含雙方將/帥，否則無法生成有意義的殘局庫
        bool hasRedKing   = false;
        bool hasBlackKing = false;
        var redExtra   = new List<PieceType>();
        var blackExtra = new List<PieceType>();

        for (int i = 0; i < 90; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone) continue;

            if (piece.Type == PieceType.King)
            {
                if (piece.Color == PieceColor.Red)   hasRedKing   = true;
                else                                  hasBlackKing = true;
                continue;
            }

            if (piece.Color == PieceColor.Red)
                redExtra.Add(piece.Type);
            else
                blackExtra.Add(piece.Type);
        }

        if (!hasRedKing || !hasBlackKing)
            throw new ArgumentException(
                "棋盤必須同時包含紅帥（Red King）與黑將（Black King）才能生成殘局庫。");

        var config = new PieceConfiguration(
            BuildDisplayName(redExtra, blackExtra), redExtra, blackExtra);

        await GenerateAsync(config, progress, cancellationToken);
    }

    public void SyncToTranspositionTable(IAiEngine engine)
    {
        if (IsGenerating)
            throw new InvalidOperationException(
                "殘局庫生成進行中，無法同步。請等待生成完成後再呼叫。");
        if (!HasBoardData)
            throw new InvalidOperationException(
                "boardIndex 為空；請先以 GenerateAsync 或 GenerateFromBoardAsync 生成殘局庫，而非從檔案匯入。");

        foreach (var (hash, board) in boardIndex)
        {
            var entry = storage.Query(hash);
            if (!entry.IsResolved || entry.Result == TablebaseResult.Draw)
                continue;

            int score = entry.Result == TablebaseResult.Win
                ? GameConstants.MateScore - entry.Depth
                : -(GameConstants.MateScore - entry.Depth);

            var bestMove = GetBestMove(board);
            engine.StoreTTEntry(hash, score, entry.Depth, bestMove ?? default);
        }
    }

    public TablebaseEntry Query(IBoard board) => storage.Query(board.ZobristKey);

    public Move? GetBestMove(IBoard board)
    {
        var currentEntry = storage.Query(board.ZobristKey);
        if (!currentEntry.IsResolved || currentEntry.Result == TablebaseResult.Draw)
            return null;

        Move? bestMove = null;
        int bestDepth = currentEntry.Result == TablebaseResult.Win ? int.MaxValue : int.MinValue;

        foreach (var move in board.GenerateLegalMoves())
        {
            board.MakeMove(move);
            var succEntry = storage.Query(board.ZobristKey);
            board.UnmakeMove(move);

            if (!succEntry.IsResolved) continue;

            if (currentEntry.Result == TablebaseResult.Win)
            {
                // 找讓對手最快負的著法（後繼 Loss 且 Depth 最小）
                if (succEntry.Result == TablebaseResult.Loss && succEntry.Depth < bestDepth)
                {
                    bestDepth = succEntry.Depth;
                    bestMove = move;
                }
            }
            else // Loss：找拖延最久的著法（後繼 Win 且 Depth 最大）
            {
                if (succEntry.Result == TablebaseResult.Win && succEntry.Depth > bestDepth)
                {
                    bestDepth = succEntry.Depth;
                    bestMove = move;
                }
            }
        }

        return bestMove;
    }

    private static string BuildDisplayName(List<PieceType> redExtra, List<PieceType> blackExtra)
    {
        static string PieceChar(PieceType t) => t switch
        {
            PieceType.Rook     => "車",
            PieceType.Horse    => "馬",
            PieceType.Cannon   => "砲",
            PieceType.Advisor  => "仕",
            PieceType.Elephant => "相",
            PieceType.Pawn     => "兵",
            _                  => "?",
        };

        var red   = string.Concat(redExtra.Select(PieceChar));
        var black = string.Concat(blackExtra.Select(t => t switch
        {
            PieceType.Advisor  => "士",
            PieceType.Elephant => "象",
            PieceType.Pawn     => "卒",
            _                  => PieceChar(t),
        }));

        return string.IsNullOrEmpty(black)
            ? $"帥{red} vs 將"
            : $"帥{red} vs 將{black}";
    }

    public async Task ExportToFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!HasTablebase)
            throw new InvalidOperationException("尚未生成殘局庫。");

        if (boardIndex.Count > 0 && currentConfig is not null)
        {
            await TablebaseSerializer.ExportWithFenAsync(
                storage, boardIndex, currentConfig, filePath, cancellationToken);
        }
        else
        {
            await TablebaseSerializer.ExportAsync(
                storage, currentConfig!, filePath, cancellationToken);
        }
    }

    public async Task<int> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        storage.Clear();
        boardIndex.Clear();
        return await TablebaseSerializer.ImportAsync(storage, filePath, cancellationToken);
    }
}
