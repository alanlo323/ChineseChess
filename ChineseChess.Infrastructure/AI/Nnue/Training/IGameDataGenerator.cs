namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 透過自動對弈生成 NNUE 訓練資料的策略介面。
/// </summary>
public interface IGameDataGenerator
{
    /// <summary>
    /// 非同步生成訓練局面資料。
    /// </summary>
    /// <param name="gameCount">要生成的對局局數。</param>
    /// <param name="searchDepth">每步使用的搜尋深度。</param>
    /// <param name="searchTimeLimitMs">每步搜尋時間上限（毫秒）。</param>
    /// <param name="onProgress">每完成一局時呼叫的進度回調（可為 null）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>收集到的所有訓練局面。</returns>
    Task<List<TrainingPosition>> GenerateAsync(
        int gameCount,
        int searchDepth,
        int searchTimeLimitMs,
        Action<GameGenerationProgress>? onProgress,
        CancellationToken ct);
}
