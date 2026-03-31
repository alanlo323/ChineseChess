using ChineseChess.Application.Models;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ChineseChess.WPF.Converters;

/// <summary>將 EloEngineType 轉換為繁體中文顯示名稱（供 ComboBox 使用）。</summary>
[ValueConversion(typeof(EloEngineType), typeof(string))]
public class EloEngineTypeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is EloEngineType t ? t switch
        {
            EloEngineType.Handcrafted => "手工評估函數",
            EloEngineType.CurrentBuiltin => "當前內建引擎",
            EloEngineType.External => "外部引擎",
            _ => value.ToString() ?? string.Empty
        } : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
