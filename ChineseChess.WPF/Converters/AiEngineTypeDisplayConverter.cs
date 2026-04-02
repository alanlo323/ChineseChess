using ChineseChess.Application.Enums;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ChineseChess.WPF.Converters;

/// <summary>將 AiEngineType 轉換為繁體中文顯示名稱（供 ComboBox 使用）。</summary>
[ValueConversion(typeof(AiEngineType), typeof(string))]
public class AiEngineTypeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AiEngineType t ? t switch
        {
            AiEngineType.Internal => "內部引擎",
            AiEngineType.External => "外部引擎",
            _ => value.ToString() ?? string.Empty
        } : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
