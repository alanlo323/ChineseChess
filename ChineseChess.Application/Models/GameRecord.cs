using System.Collections.Generic;

namespace ChineseChess.Application.Models;

/// <summary>
/// 完整棋局記錄（.ccgame 格式根物件）。
/// 以初始 FEN + 步法清單表示整局棋，可完整重播。
/// </summary>
public sealed record GameRecord
{
    /// <summary>檔案格式版本，用於向後相容性驗證。</summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>對局元資料。</summary>
    public required GameRecordMetadata Metadata { get; init; }

    /// <summary>初始局面的 FEN 字串。</summary>
    public required string InitialFen { get; init; }

    /// <summary>完整步法清單（依步號排序）。</summary>
    public required IReadOnlyList<GameRecordStep> Steps { get; init; }
}
