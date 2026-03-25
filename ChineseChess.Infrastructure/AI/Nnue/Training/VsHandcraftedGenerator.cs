using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Evaluator;
using ChineseChess.Infrastructure.AI.Search;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 透過「NNUE 對戰 HandcraftedEvaluator」生成訓練資料。
///
/// 對戰機制：
///   - 每局以 gameIndex 為種子的隨機數決定 NNUE 執紅或執黑，確保多樣性
///   - 前 <see cref="randomOpeningMoves"/> 步隨機走棋，增加開局多樣性
///   - NNUE 引擎：使用 TrainingNetworkEvaluator（worker 為 InferenceView 副本）
///   - 手工引擎：每個 worker 持有獨立的 HandcraftedEvaluator 實例（消除跨執行緒共享）
///   - Score：以 HandcraftedEvaluator.Evaluate 提供穩定的靜態評分基準
///
/// 並行設計：
///   - <paramref name="parallelism"/> &gt; 1 時同時執行多局，每局持有完全獨立的
///     nnueEngine、handEngine 及 handEvaluator，無任何可變狀態共享。
///   - 進度回報使用 Interlocked 計數，確保執行緒安全。
/// </summary>
public sealed class VsHandcraftedGenerator : IGameDataGenerator
{
    private readonly TrainingNetwork network;
    private readonly int randomOpeningMoves;
    private readonly int parallelism;

    public VsHandcraftedGenerator(TrainingNetwork network, int randomOpeningMoves = 6, int parallelism = 1)
    {
        this.network            = network;
        this.randomOpeningMoves = randomOpeningMoves;
        this.parallelism        = Math.Max(1, parallelism);
    }

    public async Task<List<TrainingPosition>> GenerateAsync(
        int gameCount,
        int searchDepth,
        int searchTimeLimitMs,
        Action<GameGenerationProgress>? onProgress,
        CancellationToken ct)
    {
        var searchSettings = new SearchSettings
        {
            Depth            = searchDepth,
            TimeLimitMs      = searchTimeLimitMs,
            ThreadCount      = 1,
            AllowOpeningBook = false,
        };

        if (parallelism <= 1)
        {
            // 單執行緒路徑：維持原本語義（TT 可跨局累積）
            var nnueEvaluator = new TrainingNetworkEvaluator(network);
            var nnueEngine    = new SearchEngine(new Application.Configuration.GameSettings(), nnueEvaluator);
            var handEvaluator = new HandcraftedEvaluator();
            var handEngine    = new SearchEngine(new Application.Configuration.GameSettings(), handEvaluator);
            var allPositions  = new List<TrainingPosition>();

            for (int gameIndex = 0; gameIndex < gameCount; gameIndex++)
            {
                if (ct.IsCancellationRequested) break;

                var gamePositions = await PlayOneGameAsync(
                    gameIndex, nnueEngine, handEngine, handEvaluator, searchSettings, ct);
                allPositions.AddRange(gamePositions);

                onProgress?.Invoke(new GameGenerationProgress
                {
                    GamesCompleted     = gameIndex + 1,
                    GamesTarget        = gameCount,
                    PositionsCollected = allPositions.Count,
                    Message            = $"生成對局 {gameIndex + 1}/{gameCount}，已收集 {allPositions.Count} 個局面",
                    IsGenerating       = gameIndex + 1 < gameCount,
                });
            }

            return allPositions;
        }

        return await ParallelGameRunner.RunAsync(
            gameCount, parallelism,
            CreateWorkerPair,
            (pair, gameIndex, innerCt) => PlayOneGameAsync(gameIndex, pair.NnueEngine, pair.HandEngine, pair.HandEvaluator, searchSettings, innerCt),
            (completed, total, positions) => $"生成對局 {completed}/{total}，已收集 {positions} 個局面",
            onProgress, ct);
    }

    private WorkerPair CreateWorkerPair()
    {
        var nnueEvaluator = new TrainingNetworkEvaluator(network);
        var nnueEngine    = new SearchEngine(new Application.Configuration.GameSettings(), nnueEvaluator);
        var handEvaluator = new HandcraftedEvaluator();
        var handEngine    = new SearchEngine(new Application.Configuration.GameSettings(), handEvaluator);
        return new WorkerPair(nnueEngine, handEngine, handEvaluator);
    }

    private async Task<List<TrainingPosition>> PlayOneGameAsync(
        int gameIndex,
        IAiEngine nnueEngine,
        IAiEngine handEngine,
        HandcraftedEvaluator handEvaluator,
        SearchSettings searchSettings,
        CancellationToken ct)
    {
        var rng = new Random(gameIndex);
        bool nnueIsRed = rng.Next(2) == 0;

        var board = new Board(GameGeneratorHelper.InitialFen);
        var gamePositions = new List<(string Fen, int Score)>();

        for (int moveCount = 0; moveCount < GameGeneratorHelper.MaxMovesPerGame; moveCount++)
        {
            if (ct.IsCancellationRequested) break;
            if (board.IsDraw() || board.IsCheckmate(board.Turn)) break;

            string fen = board.ToFen();
            int score  = handEvaluator.Evaluate(board);
            gamePositions.Add((fen, score));

            if (moveCount < randomOpeningMoves)
            {
                var legalMoves = board.GenerateLegalMoves().ToArray();
                if (legalMoves.Length == 0) break;
                board.MakeMove(legalMoves[rng.Next(legalMoves.Length)]);
                continue;
            }

            bool isNnueTurn = (board.Turn == PieceColor.Red) == nnueIsRed;
            IAiEngine activeEngine = isNnueTurn ? nnueEngine : handEngine;

            SearchResult result;
            try
            {
                result = await activeEngine.SearchAsync(board, searchSettings, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result.BestMove == default) break;
            board.MakeMove(result.BestMove);
        }

        float gameResult = GameGeneratorHelper.DetermineResult(board);
        return gamePositions
            .Select(p => new TrainingPosition { Fen = p.Fen, Score = p.Score, Result = gameResult })
            .ToList();
    }

    private sealed record WorkerPair(IAiEngine NnueEngine, IAiEngine HandEngine, HandcraftedEvaluator HandEvaluator);
}
