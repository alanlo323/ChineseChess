using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 並行對局生成的共用骨架：Channel worker pool + Parallel.ForEachAsync + 執行緒安全進度回報。
/// </summary>
internal static class ParallelGameRunner
{
    /// <summary>
    /// 以 <paramref name="parallelism"/> 並行度執行 <paramref name="gameCount"/> 局對局，
    /// 回傳合併後的所有 <see cref="TrainingPosition"/>。
    /// </summary>
    internal static async Task<List<TrainingPosition>> RunAsync<TWorker>(
        int gameCount,
        int parallelism,
        Func<TWorker> createWorker,
        Func<TWorker, int, CancellationToken, Task<List<TrainingPosition>>> playGame,
        Func<int, int, int, string> formatProgress,
        Action<GameGenerationProgress>? onProgress,
        CancellationToken ct)
    {
        var pool = Channel.CreateBounded<TWorker>(parallelism);
        for (int i = 0; i < parallelism; i++)
            pool.Writer.TryWrite(createWorker());

        int completedGames = 0;
        int totalPositions = 0;
        var bag = new ConcurrentBag<List<TrainingPosition>>();   // 每局整批加入，減少鎖定次數

        await Parallel.ForEachAsync(
            Enumerable.Range(0, gameCount),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (gameIndex, innerCt) =>
            {
                var worker = await pool.Reader.ReadAsync(innerCt);
                try
                {
                    var gamePositions = await playGame(worker, gameIndex, innerCt);
                    bag.Add(gamePositions);

                    int completed = Interlocked.Increment(ref completedGames);
                    int positions = Interlocked.Add(ref totalPositions, gamePositions.Count);

                    onProgress?.Invoke(new GameGenerationProgress
                    {
                        GamesCompleted     = completed,
                        GamesTarget        = gameCount,
                        PositionsCollected = positions,
                        Message            = formatProgress(completed, gameCount, positions),
                        IsGenerating       = completed < gameCount,
                    });
                }
                finally
                {
                    bool returned = pool.Writer.TryWrite(worker);
                    Debug.Assert(returned,
                        "Worker pool 歸還失敗：Channel 容量不足，這不應發生（capacity == parallelism）");
                }
            });

        return bag.SelectMany(list => list).ToList();
    }
}
