using ChineseChess.Domain.Enums;
using System;
using System.Globalization;
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
