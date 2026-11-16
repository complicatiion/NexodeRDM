using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Nexode.App.Views;

public static class Converters
{
    public static readonly IValueConverter BoolToSemiBold =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>value?.ToString() == parameter.</summary>
    public static readonly IValueConverter Equal = new EqualsConverter();

    public static readonly IValueConverter NotNull = new FuncValueConverter<object?, bool>(o => o is not null);

    private sealed class EqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
    }
}
