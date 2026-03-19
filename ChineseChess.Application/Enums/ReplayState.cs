namespace ChineseChess.Application.Enums;

/// <summary>棋局重播狀態機。</summary>
public enum ReplayState
{
    /// <summary>正常對弈中，非重播模式。</summary>
    Live,

    /// <summary>重播模式中，棋盤定格於某一步，AI 暫停走棋。</summary>
    Replaying,

    /// <summary>從重播局面繼續（中途換手），轉換中的短暫狀態，隨即轉為 Live。</summary>
    Branching,
}
