using System;
using System.Globalization;
using System.Windows.Data;
using ChineseChess.Application.Enums;

namespace ChineseChess.WPF.Converters;

/// <summary>
/// 將 GameMode enum 轉換為中文顯示名稱。
/// </summary>
public class GameModeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is GameMode mode)
        {
            return mode switch
            {
                GameMode.PlayerVsAi => "人 vs AI",
                GameMode.PlayerVsPlayer => "人 vs 人",
                GameMode.AiVsAi => "AI vs AI",
                _ => mode.ToString(),
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
