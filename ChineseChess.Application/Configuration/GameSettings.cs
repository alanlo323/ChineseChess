namespace ChineseChess.Application.Configuration;

/// <summary>
/// 遊戲可配置初始設定，從 appsettings.json 載入。
/// </summary>
public class GameSettings
{
    // ─── 全域 AI 設定（非 AiVsAi 模式）──────────────────────────────────
    public int SearchDepth { get; set; } = 6;
    public int SearchThinkingTimeSeconds { get; set; } = 3;

    // ─── AiVsAi 紅方設定 ────────────────────────────────────────────────
    public int RedSearchDepth { get; set; } = 6;
    public int RedSearchThinkingTimeSeconds { get; set; } = 3;

    // ─── AiVsAi 黑方設定 ────────────────────────────────────────────────
    public int BlackSearchDepth { get; set; } = 6;
    public int BlackSearchThinkingTimeSeconds { get; set; } = 3;

    // ─── TT 設定 ─────────────────────────────────────────────────────────
    public bool UseSharedTranspositionTable { get; set; } = false;
    public int TranspositionTableSizeMb { get; set; } = 64;

    // ─── 智能提示 ─────────────────────────────────────────────────────────
    public bool IsSmartHintEnabled { get; set; } = true;
    public int SmartHintDepth { get; set; } = 2;
}
