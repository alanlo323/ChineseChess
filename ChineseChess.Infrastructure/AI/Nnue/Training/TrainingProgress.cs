namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 訓練進度快照，供 UI 層綁定。
/// </summary>
public sealed class TrainingProgress
{
    /// <summary>當前 epoch（從 1 開始）。</summary>
    public int Epoch { get; init; }

    /// <summary>當前 epoch 內的步數（從 1 開始）。</summary>
    public int Step { get; init; }

    /// <summary>整體步數（跨 epoch 累計）。</summary>
    public long TotalSteps { get; init; }

    /// <summary>本批次的訓練損失（WDL loss）。</summary>
    public float Loss { get; init; }

    /// <summary>歷史最低損失（觸發存檔的閾值）。</summary>
    public float BestLoss { get; init; }

    /// <summary>當前學習率。</summary>
    public float LearningRate { get; init; }

    /// <summary>預估剩餘時間（秒），-1 表示無法估計。</summary>
    public double EtaSeconds { get; init; }

    /// <summary>訓練狀態訊息（供 log 視窗顯示）。</summary>
    public string? Message { get; init; }

    /// <summary>訓練是否正在進行中。</summary>
    public bool IsRunning { get; init; }
}
