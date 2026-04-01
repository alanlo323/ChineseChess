using System;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 紅方與黑方 AI 設定 ViewModel 的持有者（供 DI 容器註冊用，因 ValueTuple 非參考型別）。
/// </summary>
public sealed class AiPlayerSettingsHolder(
    AiPlayerSettingsViewModel red,
    AiPlayerSettingsViewModel black)
{
    public AiPlayerSettingsViewModel Red { get; } = red ?? throw new ArgumentNullException(nameof(red));
    public AiPlayerSettingsViewModel Black { get; } = black ?? throw new ArgumentNullException(nameof(black));
}
