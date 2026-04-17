using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CLCKTY.Core;

namespace CLCKTY.UI.Components;

public sealed class SoundBadgeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush StockBrush = new(System.Windows.Media.Color.FromRgb(88, 176, 255));
    private static readonly SolidColorBrush ImportedBrush = new(System.Windows.Media.Color.FromRgb(132, 246, 184));
    private static readonly SolidColorBrush RecordedBrush = new(System.Windows.Media.Color.FromRgb(255, 197, 120));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SoundBadge badge)
        {
            return System.Windows.Media.Brushes.Gray;
        }

        return badge switch
        {
            SoundBadge.Stock => StockBrush,
            SoundBadge.Imported => ImportedBrush,
            SoundBadge.Recorded => RecordedBrush,
            _ => System.Windows.Media.Brushes.Gray
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
