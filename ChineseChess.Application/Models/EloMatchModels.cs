using System.Collections.Generic;

namespace ChineseChess.Application.Models;

// ── 引擎選擇類型 ──

/// <summary>Elo 評估中可選的引擎類型。</summary>
public enum EloEngineType
{
    /// <summary>手工評估函數引擎（HandcraftedEvaluator）。</summary>
    Handcrafted,

    /// <summary>當前內建引擎（若 NNUE 已載入則使用 NNUE，否則 fallback 至手工評估）。</summary>
    CurrentBuiltin,

    /// <summary>外部引擎（如 Pikafish，需在「外部引擎」Tab 中先設定）。</summary>
    External
}

// ── 對弈設定 ──

/// <summary>Elo 對弈評估的完整設定。</summary>
public class EloMatchSettings
{
    /// <summary>總對弈局數（10–500）。</summary>
    public int TotalGames { get; init; } = 100;

    // 引擎 A 設定
    public EloEngineType EngineAType { get; init; } = EloEngineType.CurrentBuiltin;
    public int EngineADepth { get; init; } = 6;
    public int EngineATimeLimitMs { get; init; } = 3000;

    // 引擎 B 設定
    public EloEngineType EngineBType { get; init; } = EloEngineType.External;
    public int EngineBDepth { get; init; } = 6;
    public int EngineBTimeLimitMs { get; init; } = 3000;

    // 裁決規則
    /// <summary>每局最大步數。超過後判和。</summary>
    public int MaxMovesPerGame { get; init; } = 200;

    /// <summary>無吃子和棋半步數門檻（對應 Board.IsDrawByNoCapture 的 limit 參數）。</summary>
    public int NoCaptureDrawLimit { get; init; } = 120;

    /// <summary>認輸門檻（分）。分數絕對值超過此值時開始計算連續步數。</summary>
    public int ResignThresholdCp { get; init; } = 1500;

    /// <summary>需連續超過認輸門檻幾步才判定認輸。</summary>
    public int ResignConsecutiveMoves { get; init; } = 5;
}

// ── 單局結果 ──

/// <summary>單局對弈的勝負結果（從引擎 A 視角）。</summary>
public enum GameOutcome
{
    EngineAWin,
    EngineBWin,
    Draw
}

/// <summary>對局終止原因。</summary>
public enum TerminationReason
{
    /// <summary>將殺（被將死）。</summary>
    Checkmate,

    /// <summary>困斃（無合法走法且未被將軍，象棋規則中等同輸棋）。</summary>
    Stalemate,

    /// <summary>重複局面和棋。</summary>
    DrawByRepetition,

    /// <summary>無吃子步數達上限（皮卡魚規則）。</summary>
    DrawByNoCapture,

    /// <summary>棋子不足和棋。</summary>
    DrawByInsufficientMaterial,

    /// <summary>認輸（分數長期超過門檻）。</summary>
    Resignation,

    /// <summary>超過最大步數限制，判和。</summary>
    MaxMoves
}

/// <summary>單局對弈結果。</summary>
public class SingleGameResult
{
    public int GameNumber { get; init; }

    /// <summary>true = 引擎 A 執紅；false = 引擎 A 執黑。</summary>
    public bool EngineAPlaysRed { get; init; }

    public GameOutcome Outcome { get; init; }
    public TerminationReason Reason { get; init; }

    /// <summary>對局總步數（雙方合計）。</summary>
    public int TotalMoves { get; init; }

    /// <summary>對局結束時的 FEN 字串。</summary>
    public string FinalFen { get; init; } = string.Empty;
}

// ── 即時統計 ──

/// <summary>Elo 對弈的即時統計數據。</summary>
public class EloMatchStatistics
{
    public int GamesPlayed { get; init; }
    public int EngineAWins { get; init; }
    public int EngineBWins { get; init; }
    public int Draws { get; init; }

    /// <summary>引擎 A 勝率（0.0–1.0）。</summary>
    public double EngineAWinRate { get; init; }

    /// <summary>引擎 A 的得分率：(勝 + 和×0.5) / 總局數。</summary>
    public double EngineAScore { get; init; }

    /// <summary>引擎 A 相對於引擎 B 的 Elo 差距（正值 = A 較強）。</summary>
    public double EloDifference { get; init; }

    /// <summary>95% 信賴區間下界。</summary>
    public double EloConfidenceLow { get; init; }

    /// <summary>95% 信賴區間上界。</summary>
    public double EloConfidenceHigh { get; init; }

    /// <summary>平均每局步數。</summary>
    public double AverageGameLength { get; init; }

    /// <summary>樣本數不足 30 時為 true，提示 Elo 估算可能不穩定。</summary>
    public bool IsLowSampleWarning { get; init; }
}

// ── 進度回報 ──

/// <summary>對弈進度，透過 IProgress&lt;EloMatchProgress&gt; 回報給 UI 層。</summary>
public class EloMatchProgress
{
    /// <summary>當前進行中的局數（1-based）。</summary>
    public int CurrentGameNumber { get; init; }

    public int TotalGames { get; init; }

    /// <summary>true = 引擎 A 本局執紅。</summary>
    public bool EngineAPlaysRed { get; init; }

    /// <summary>當前局已走步數。</summary>
    public int CurrentMoveCount { get; init; }

    /// <summary>當前局面的 FEN 字串。</summary>
    public string CurrentFen { get; init; } = string.Empty;

    /// <summary>上一局結果。第一局開始時為 null。</summary>
    public SingleGameResult? LastGameResult { get; init; }

    /// <summary>截至目前的累計統計數據。</summary>
    public EloMatchStatistics RunningStats { get; init; } = new();
}
