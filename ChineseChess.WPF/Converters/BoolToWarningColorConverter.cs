using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ChineseChess.WPF.Converters;

/// <summary>true → 橙色警告；false → 正常深灰色。</summary>
public class BoolToWarningColorConverter : IValueConverter
{
    private static readonly SolidColorBrush WarningBrush = CreateFrozen(0xCC, 0x55, 0x00);
    private static readonly SolidColorBrush NormalBrush  = CreateFrozen(0x44, 0x44, 0x44);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool isWarning && isWarning ? WarningBrush : NormalBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
