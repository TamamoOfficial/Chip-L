using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ChipL;

/// <summary>把 "#AARRGGBB" / "#RRGGBB" 形式的字符串转成画刷（用于数据绑定颜色）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return Brushes.Transparent;
        try
        {
            var obj = ColorConverter.ConvertFromString(s);
            return obj is Color c ? new SolidColorBrush(c) : Brushes.Transparent;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
