using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AlpacaDashboard;

/// <summary>Maps a signed number to the up/down/neutral text brush.</summary>
public sealed class SignBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var sign = ToDecimal(value) is { } d ? Math.Sign(d) : 0;
        var key = sign > 0 ? "GoodBrush" : sign < 0 ? "BadBrush" : "SecondaryBrush";
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static decimal? ToDecimal(object? value) => value switch
    {
        decimal d => d,
        double d => (decimal)d,
        int i => i,
        _ => null,
    };
}

/// <summary>
/// Formats a signed number with an explicit arrow + sign so direction never relies on color alone.
/// ConverterParameter: "C" (currency, default) or "P" (percent of a fraction).
/// </summary>
public sealed class SignedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (SignBrushConverter.ToDecimal(value) is not { } d) return "—";
        var abs = Math.Abs(d);
        var text = (parameter as string) == "P" ? abs.ToString("P2", culture) : abs.ToString("C2", culture);
        return d > 0 ? $"▲ +{text}" : d < 0 ? $"▼ −{text}" : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound enum value's name equals the ConverterParameter.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is non-null / non-empty (or the reverse with ConverterParameter="Invert").</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            int i => i > 0,
            _ => true,
        };
        if ((parameter as string) == "Invert") hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
