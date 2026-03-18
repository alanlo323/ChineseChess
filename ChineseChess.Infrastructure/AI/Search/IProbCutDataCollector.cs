using System.IO;

namespace ChineseChess.Infrastructure.AI.Search;

/// <summary>
/// ProbCut 回歸資料收集器介面。
/// 在 DataCollectionMode 啟用時，SearchWorker 會透過此介面記錄觀測樣本。
/// Production 模式下使用 NullProbCutDataCollector（空操作），零額外開銷。
/// </summary>
public interface IProbCutDataCollector
{
    /// <summary>記錄一筆 ProbCut 觀測樣本。</summary>
    void RecordSample(ProbCutSample sample);

    /// <summary>將所有樣本匯出為 CSV 格式（含標頭行）。</summary>
    void ExportCsv(TextWriter writer);

    /// <summary>目前已收集的樣本總數。</summary>
    int SampleCount { get; }
}
