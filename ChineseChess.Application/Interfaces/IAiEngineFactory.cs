using ChineseChess.Application.Configuration;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 建立 AI 引擎實例的工廠介面。
/// 允許 Application 層要求「帶特定 NNUE 設定的引擎」，
/// 而不直接依賴 Infrastructure 的具體評估器型別（CompositeEvaluator、NnueNetwork 等）。
/// </summary>
public interface IAiEngineFactory
{
    /// <summary>
    /// 建立一個使用指定 NNUE 設定的引擎（非同步，因為需要載入模型檔）。
    /// </summary>
    /// <param name="config">NNUE 設定（模型路徑、評估模式）。</param>
    /// <param name="ct">取消 Token。</param>
    Task<IAiEngine> CreateWithNnueAsync(NnueEngineConfig config, CancellationToken ct = default);

    /// <summary>
    /// 建立一個僅使用手工評估器的引擎（無需非同步）。
    /// </summary>
    IAiEngine CreateWithHandcrafted();
}
