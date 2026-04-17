using System.Globalization;
using System.Windows.Data;

namespace CLCKTY.UI.Components;

public sealed class BoolToProtectionTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool canDelete && canDelete)
        {
            return "Deletable";
        }

        return "Protected";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
