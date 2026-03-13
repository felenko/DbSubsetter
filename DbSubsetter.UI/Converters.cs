using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DbSubsetter.UI;

public class BoolToCheckMarkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "✓" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts (stepNumber, CurrentStep) to the stroke brush for the step circle.
/// Current and visited steps: green. Not yet visited: blue.</summary>
public class StepCircleBrushConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x22, 0xC5, 0x5E)); // current + visited
    private static readonly SolidColorBrush Blue  = new(Color.FromRgb(0x4F, 0x46, 0xE5)); // not visited

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is null) return Blue;
        int step = System.Convert.ToInt32(values[0], culture);
        int currentStep = values[1] is int c ? c : 1;

        // Current or already visited: green
        if (step <= currentStep)
            return Green;
        // Not yet visited: blue
        return Blue;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
