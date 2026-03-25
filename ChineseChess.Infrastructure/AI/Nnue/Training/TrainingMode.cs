namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// NNUE 訓練資料來源模式。
/// </summary>
public enum TrainingMode
{
    /// <summary>從 .plain 檔案載入靜態對局資料（預設）。</summary>
    FromFile,

    /// <summary>NNUE 引擎與 HandcraftedEvaluator 對戰，動態生成訓練資料。</summary>
    VsHandcrafted,

    /// <summary>兩方皆使用當前 TrainingNetwork 自我對戰，生成多樣化訓練資料。</summary>
    SelfPlay,
}
