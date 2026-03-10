namespace ChineseChess.Application.Models;

/// <summary>提和來源：玩家主動提和或 AI 主動提和。</summary>
public enum DrawOfferSource
{
    Player,
    Ai
}

/// <summary>提和結果（不可變）。</summary>
public sealed record DrawOfferResult(DrawOfferSource Source, bool Accepted, string Reason);

/// <summary>提和相關設定。</summary>
public sealed record DrawOfferSettings
{
    /// <summary>均勢判定門檻：|score| ≤ 此值時視為均勢。</summary>
    public int DrawOfferThreshold { get; init; } = 50;

    /// <summary>AI 拒絕玩家提和的門檻：AI 佔優超過此值則拒絕。</summary>
    public int DrawRefuseThreshold { get; init; } = 100;

    /// <summary>AI 主動提和的最低步數。</summary>
    public int MinMoveCountForAiDrawOffer { get; init; } = 30;

    /// <summary>AI 提和冷卻步數：拒絕後需等待此步數才能再次提和。</summary>
    public int CooldownMoves { get; init; } = 10;
}
