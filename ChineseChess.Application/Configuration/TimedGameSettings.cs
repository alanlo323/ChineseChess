using System;

namespace ChineseChess.Application.Configuration;

/// <summary>
/// 限時遊戲設定：控制棋鐘是否啟用及每方的時間配額。
/// </summary>
public sealed record TimedGameSettings
{
    /// <summary>是否啟用限時模式。預設關閉。</summary>
    public bool IsEnabled { get; init; } = false;

    /// <summary>每方可用時間。預設 10 分鐘。</summary>
    public TimeSpan TimePerPlayer { get; init; } = TimeSpan.FromMinutes(10);
}
