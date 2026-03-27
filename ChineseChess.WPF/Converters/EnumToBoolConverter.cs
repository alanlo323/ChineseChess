using System;
using System.Globalization;
using System.Windows.Data;

namespace ChineseChess.WPF.Converters;

/// <summary>
/// 讓 RadioButton 綁定 enum 屬性。
/// ConverterParameter 傳入 enum 值的字串，與目前值比較。
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter != null)
        {
            try
            {
                return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
            }
            catch (ArgumentException)
            {
                return Binding.DoNothing;
            }
        }
        return Binding.DoNothing;
    }
}
