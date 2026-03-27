using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ChineseChess.WPF.Converters;

/// <summary>true → 綠色（驗證通過）；false → 紅色（驗證失敗）。</summary>
public class BoolToValidationColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isValid && isValid)
            return new SolidColorBrush(Color.FromRgb(0x22, 0x88, 0x22));
        return new SolidColorBrush(Color.FromRgb(0xCC, 0x22, 0x00));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
