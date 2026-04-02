using ChineseChess.Application.Enums;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ChineseChess.WPF.Converters;

/// <summary>將 InternalEvaluatorType 轉換為繁體中文顯示名稱（供 ComboBox 使用）。</summary>
[ValueConversion(typeof(InternalEvaluatorType), typeof(string))]
public class InternalEvaluatorTypeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is InternalEvaluatorType t ? t switch
        {
            InternalEvaluatorType.Handcrafted => "手工評估函式",
            InternalEvaluatorType.Nnue        => "NNUE 神經網路",
            _ => value.ToString() ?? string.Empty
        } : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
