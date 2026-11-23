using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ZapretControl.Core;

namespace ZapretControl.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert)
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasText = value is string s && !string.IsNullOrWhiteSpace(s);
        if (Invert)
            hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Frozen(0xFF, 0x39, 0xD9, 0x8A);
    private static readonly Brush Warn = Frozen(0xFF, 0xF2, 0xD0, 0x6B);
    private static readonly Brush Fail = Frozen(0xFF, 0xFF, 0x3B, 0x5C);
    private static readonly Brush Info = Frozen(0xFF, 0x00, 0xE5, 0xFF);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is CheckStatus status
            ? status switch
            {
                CheckStatus.Ok => Ok,
                CheckStatus.Warn => Warn,
                CheckStatus.Fail => Fail,
                _ => Info
            }
            : Info;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    private static readonly Brush Info = Frozen(0xFF, 0xF5, 0xEE, 0xF7);
    private static readonly Brush Warn = Frozen(0xFF, 0xF2, 0xD0, 0x6B);
    private static readonly Brush Error = Frozen(0xFF, 0xFF, 0x3B, 0x5C);
    private static readonly Brush Success = Frozen(0xFF, 0x39, 0xD9, 0x8A);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is LogLevel level
            ? level switch
            {
                LogLevel.Warn => Warn,
                LogLevel.Error => Error,
                LogLevel.Success => Success,
                _ => Info
            }
            : Info;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
