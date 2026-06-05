namespace StageManager.Converters
{
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;

    /// <summary>
    /// Converts an index to a <see cref="Thickness"/> so that each successive item is offset
    /// slightly, creating a stacked preview effect (e.g., 6 px right & down per index).
    /// </summary>
    public sealed class IndexToOffsetMarginConverter : IValueConverter
    {
        private const double Step = 10; // distance in pixels per index step

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index)
            {
                double offset = index * Step;
                return new Thickness(offset, offset, 0, 0);
            }

            return new Thickness(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
} 