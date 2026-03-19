namespace ChineseChess.Application.Models;

/// <summary>棋局記錄中的單步走法（可序列化）。</summary>
public sealed record GameRecordStep
{
    /// <summary>步號（1-based）。</summary>
    public required int StepNumber { get; init; }

    /// <summary>起始格索引（0–89）。</summary>
    public required byte From { get; init; }

    /// <summary>目標格索引（0–89）。</summary>
    public required byte To { get; init; }

    /// <summary>中文記譜，如「炮二平五」。</summary>
    public required string Notation { get; init; }

    /// <summary>走棋方顏色字串："Red" 或 "Black"。</summary>
    public required string Turn { get; init; }

    /// <summary>是否為吃子著法。</summary>
    public bool IsCapture { get; init; }
}
