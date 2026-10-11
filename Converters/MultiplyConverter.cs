using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Deduplicator.Converters;

/// <summary>
/// 乘法转换器，用于将值乘以指定的参数
/// </summary>
public class MultiplyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double doubleValue && parameter is string paramString && double.TryParse(paramString, out double factor))
        {
            return doubleValue * factor;
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double doubleValue && parameter is string paramString && double.TryParse(paramString, out double factor) && factor != 0)
        {
            return doubleValue / factor;
        }
        return value;
    }
}