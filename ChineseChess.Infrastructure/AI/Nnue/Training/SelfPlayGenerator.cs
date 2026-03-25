using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Evaluator;
using ChineseChess.Infrastructure.AI.Search;


namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 透過「自我對戰」生成訓練資料：兩方皆使用當前 TrainingNetwork 評估。
///
/// 隨機性設計：
///   - 前 <paramref name="randomOpeningMoves"/> 步從合法著法中隨機選取，
///     以 gameIndex 作為種子確保每局不同但可重現。
///   - 此設計可避免所有對局從完全相同的路徑出發，增加訓練資料多樣性。
///
/// 並行設計：
///   - <paramref name="parallelism"/> &gt; 1 時同時執行多局，每局持有獨立的
///     <see cref="TrainingNetworkEvaluator"/> 和 <see cref="SearchEngine"/>，
///     透過 <see cref="TrainingNetworkInferenceView"/> 共享 weights（唯讀），無競爭。
///   - 進度回報使用 Interlocked 計數，確保執行緒安全。
/// </summary>
public sealed class SelfPlayGenerator : IGameDataGenerator
{
    private readonly TrainingNetwork network;
    private readonly int randomOpeningMoves;
    private readonly int parallelism;

    public SelfPlayGenerator(TrainingNetwork network, int randomOpeningMoves = 8, int parallelism = 1)
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
            ThreadCount      = 1,   // 每個 worker 的引擎單執行緒，並行由局層級管理
            AllowOpeningBook = false,
        };

        if (parallelism <= 1)
        {
            // 單執行緒路徑：維持原本語義（TT 可跨局累積）
            var evaluator = new TrainingNetworkEvaluator(network);
            var engine    = new SearchEngine(new Application.Configuration.GameSettings(), evaluator);
            var allPositions = new List<TrainingPosition>();

            for (int gameIndex = 0; gameIndex < gameCount; gameIndex++)
            {
                if (ct.IsCancellationRequested) break;

                var gamePositions = await PlayOneGameAsync(gameIndex, evaluator, engine, searchSettings, ct);
                allPositions.AddRange(gamePositions);

                onProgress?.Invoke(new GameGenerationProgress
                {
                    GamesCompleted     = gameIndex + 1,
                    GamesTarget        = gameCount,
                    PositionsCollected = allPositions.Count,
                    Message            = $"自我對戰 {gameIndex + 1}/{gameCount}，已收集 {allPositions.Count} 個局面",
                    IsGenerating       = gameIndex + 1 < gameCount,
                });
            }

            return allPositions;
        }

        return await ParallelGameRunner.RunAsync(
            gameCount, parallelism,
            () => CreateWorkerPair(searchSettings),
            (pair, gameIndex, innerCt) => PlayOneGameAsync(gameIndex, pair.Evaluator, pair.Engine, searchSettings, innerCt),
            (completed, total, positions) => $"自我對戰 {completed}/{total}，已收集 {positions} 個局面",
            onProgress, ct);
    }

    private WorkerPair CreateWorkerPair(SearchSettings searchSettings)
    {
        var evaluator = new TrainingNetworkEvaluator(network);
        var engine    = new SearchEngine(new Application.Configuration.GameSettings(), evaluator);
        return new WorkerPair(evaluator, engine);
    }

    private async Task<List<TrainingPosition>> PlayOneGameAsync(
        int gameIndex,
        TrainingNetworkEvaluator evaluator,
        IAiEngine engine,
        SearchSettings searchSettings,
        CancellationToken ct)
    {
        var rng = randomOpeningMoves > 0 ? new Random(gameIndex) : null;

        var board = new Board(GameGeneratorHelper.InitialFen);
        var gamePositions = new List<(string Fen, int Score)>();

        for (int moveCount = 0; moveCount < GameGeneratorHelper.MaxMovesPerGame; moveCount++)
        {
            if (ct.IsCancellationRequested) break;
            if (board.IsDraw() || board.IsCheckmate(board.Turn)) break;

            string fen   = board.ToFen();
            int score    = evaluator.Evaluate(board);
            gamePositions.Add((fen, score));

            if (rng != null && moveCount < randomOpeningMoves)
            {
                var legalMoves = board.GenerateLegalMoves().ToArray();
                if (legalMoves.Length == 0) break;

                var randomMove = legalMoves[rng.Next(legalMoves.Length)];
                board.MakeMove(randomMove);
            }
            else
            {
                Application.Interfaces.SearchResult result;
                try
                {
                    result = await engine.SearchAsync(board, searchSettings, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (result.BestMove == default) break;
                board.MakeMove(result.BestMove);
            }
        }

        float gameResult = GameGeneratorHelper.DetermineResult(board);
        return gamePositions
            .Select(p => new TrainingPosition { Fen = p.Fen, Score = p.Score, Result = gameResult })
            .ToList();
    }

    private sealed record WorkerPair(TrainingNetworkEvaluator Evaluator, IAiEngine Engine);
}
