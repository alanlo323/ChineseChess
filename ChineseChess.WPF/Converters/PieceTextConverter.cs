using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ChineseChess.WPF.Converters;

public class PieceTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Piece piece || piece.IsNone)
        {
            return string.Empty;
        }

        return piece.Type switch
        {
            PieceType.King => piece.Color == PieceColor.Red ? "帥" : "將",
            PieceType.Advisor => piece.Color == PieceColor.Red ? "仕" : "士",
            PieceType.Elephant => piece.Color == PieceColor.Red ? "相" : "象",
            PieceType.Horse => "馬",
            PieceType.Rook => "俥",
            PieceType.Cannon => "砲",
            PieceType.Pawn => piece.Color == PieceColor.Red ? "兵" : "卒",
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
