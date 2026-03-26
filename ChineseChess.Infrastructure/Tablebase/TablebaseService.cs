using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;

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

    public bool HasTablebase => storage.TotalPositions > 0;
    public PieceConfiguration? CurrentConfiguration => currentConfig;
    public bool IsGenerating => Volatile.Read(ref isGeneratingFlag) == 1;

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

    public TablebaseEntry Query(IBoard board) => storage.Query(board.ZobristKey);

    public Move? GetBestMove(Board board)
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
            board.UndoMove();

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
