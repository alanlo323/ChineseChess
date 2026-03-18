namespace ChineseChess.Infrastructure.AI.Search;

/// <summary>
/// 一筆 ProbCut 搜尋觀測樣本，用於後續回歸分析。
///
/// 欄位說明：
///   ShallowScore  — QSearch 淺層分數（第一階段估算）
///   DeepScore     — Negamax 精確分數（第二階段確認）
///   BetaUsed      — probBeta 值（= beta + ProbCutMargin）
///   Depth         — 觸發 ProbCut 時的主搜尋深度
///   Ply           — 觸發時距根節點的距離
///   DepthPair     — (depth, depth - ProbCutReduction - 1) 配對分類
///   Phase         — 棋局階段（開局/中局/殘局）
///   CaptureClass  — 吃子棋子類型分類
/// </summary>
public readonly record struct ProbCutSample(
    int ShallowScore,
    int DeepScore,
    int BetaUsed,
    int Depth,
    int Ply,
    ProbCutDepthPair DepthPair,
    ProbCutPhase Phase,
    ProbCutCaptureClass CaptureClass
);

/// <summary>
/// 深度配對分類（主深度 vs 淺搜深度 = 主深度 - ProbCutReduction - 1）。
/// ProbCutReduction = 4，故 depth=5 → 淺搜 depth=0（D5_0），depth=6 → D6_1，以此類推。
/// </summary>
public enum ProbCutDepthPair
{
    D5_0,
    D6_1,
    D7_2,
    D8_3,
    D9_4,
    D10Plus
}

/// <summary>棋局階段分類（依 GamePhase.Calculate() 回傳的 0–256 值）。</summary>
public enum ProbCutPhase
{
    /// <summary>Opening：GamePhase >= 200（接近完整材料）。</summary>
    Opening,
    /// <summary>Midgame：80 ≤ GamePhase &lt; 200。</summary>
    Midgame,
    /// <summary>Endgame：GamePhase &lt; 80。</summary>
    Endgame
}

/// <summary>吃子著法中進攻方棋子的類型分類。</summary>
public enum ProbCutCaptureClass
{
    RookCapture,
    CannonCapture,
    HorseCapture,
    MinorCapture,   // 仕/士、象/相
    PawnCapture
}
