using ChineseChess.Application.Interfaces;

namespace ChineseChess.Tests.Helpers;

/// <summary>
/// 擴充 IGameService 以暴露測試輔助方法，避免汙染正式公開介面（ISP）。
/// GameService 具體型別繼續實作這些方法；測試中使用具體型別，無需轉型。
/// </summary>
public interface ITestableGameService : IGameService
{
    /// <summary>（測試用）模擬 AI 已提和，設定待回應狀態。</summary>
    void SimulateAiDrawOffer();
}
