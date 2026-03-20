namespace ChineseChess.Application.Enums;

/// <summary>
/// Pikafish 引擎的評分顯示方式。
/// 對應 UCI 選項 "ScoreType"。
/// </summary>
public enum PikafishScoreType
{
    Elo,
    PawnValueNormalized,
    Raw
}
