using System.Globalization;
using System.Windows.Data;

namespace DbSubsetter.UI;

public class BoolToCheckMarkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "✓" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
