using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace readingFish.Views;

/// <summary>Конвертеры страницы проекта.</summary>
public static class Converters
{
    /// <summary>Жирный шрифт вкл/выкл.</summary>
    public static readonly IValueConverter BoldToFontWeight = new BoolToFontWeightConverter();
}

internal sealed class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeight.Bold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FontWeight weight && weight == FontWeight.Bold;
}
