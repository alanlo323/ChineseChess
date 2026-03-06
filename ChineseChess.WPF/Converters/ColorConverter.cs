using ChineseChess.Domain.Enums;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ChineseChess.WPF.Converters;

public class ColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PieceColor color)
        {
            return color switch
            {
                PieceColor.Red => Brushes.Red,
                PieceColor.Black => Brushes.Black,
                _ => Brushes.Transparent
            };
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>bool → Visibility 反轉（true → Collapsed，false → Visible）</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>
/// 將走法評分（int）轉換為對應的顏色 Brush：正分=綠、負分=紅、零=深灰
/// </summary>
public class SmartHintScoreToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Positive = new(Color.FromRgb(0x69, 0xFF, 0x47));  // #69FF47 亮綠（黑底上可見）
    private static readonly SolidColorBrush Negative = new(Color.FromRgb(0xFF, 0x55, 0x55));  // #FF5555 亮紅
    private static readonly SolidColorBrush Neutral  = new(Color.FromRgb(0xCC, 0xCC, 0xCC));  // #CCCCCC 淺灰

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int score)
        {
            return score > 0 ? Positive : score < 0 ? Negative : Neutral;
        }
        return Neutral;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
