using ChineseChess.Application.Configuration;
using ChineseChess.Domain.Entities;

namespace ChineseChess.Infrastructure.AI.Nnue.Network;

/// <summary>
/// NNUE 推論介面。
/// 實作須為無狀態（可被多個 SearchWorker 共用），
/// 累加器狀態由呼叫端的 NnueAccumulator 實例管理。
/// </summary>
public interface INnueNetwork
{
    /// <summary>是否已成功載入模型。</summary>
    bool IsLoaded { get; }

    /// <summary>已載入模型的元數據（未載入時為 null）。</summary>
    NnueModelInfo? ModelInfo { get; }

    /// <summary>已載入的網路權重（未載入時為 null）。供 NnueAccumulator 存取。</summary>
    NnueWeights? Weights { get; }

    /// <summary>從檔案路徑非同步載入模型。</summary>
    /// <exception cref="InvalidDataException">格式錯誤或版本不符。</exception>
    /// <exception cref="FileNotFoundException">檔案不存在。</exception>
    Task LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>從已快取的 NnueWeights 物件直接設定（不重新讀檔，共享記憶體）。</summary>
    void LoadFromWeights(NnueWeights sharedWeights, NnueModelInfo info);

    /// <summary>卸載目前模型，釋放佔用記憶體。</summary>
    void Unload();

    /// <summary>
    /// 對已刷新的累加器執行前向推論，回傳以 centipawns 為單位的評分。
    /// 正值代表 board.Turn 方有利。
    /// </summary>
    int Evaluate(IBoard board, NnueAccumulator accumulator);
}
