using ChineseChess.Application.Enums;

namespace ChineseChess.Application.Models;

/// <summary>棋局記錄的元資料（對局資訊）。</summary>
public sealed record GameRecordMetadata
{
    public string RedPlayer { get; init; } = "玩家";
    public string BlackPlayer { get; init; } = "AI";
    public string Date { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public GameMode GameMode { get; init; } = GameMode.PlayerVsAi;
}
