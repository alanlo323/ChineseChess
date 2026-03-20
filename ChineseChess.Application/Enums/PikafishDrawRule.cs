namespace ChineseChess.Application.Enums;

/// <summary>
/// Pikafish 引擎的和棋規則設定。
/// 對應 UCI 選項 "DrawRule"。
/// </summary>
public enum PikafishDrawRule
{
    None,
    DrawAsBlackWin,
    DrawAsRedWin,
    DrawRepAsBlackWin,
    DrawRepAsRedWin
}
