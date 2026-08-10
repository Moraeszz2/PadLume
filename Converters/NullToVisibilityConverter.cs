using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Padlume
{
    /// <summary>
    /// null -> Collapsed, non-null value -> Visible. Use ConverterParameter="Invert" for the opposite
    /// behavior (useful for showing a fallback icon only when there's no image).
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hasValue = value != null;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
                hasValue = !hasValue;
            return hasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
