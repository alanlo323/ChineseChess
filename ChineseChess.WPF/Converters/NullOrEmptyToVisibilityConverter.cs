using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ChineseChess.WPF.Converters;

/// <summary>null 或空字串 → Collapsed；有值 → Visible。</summary>
public class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s))
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
