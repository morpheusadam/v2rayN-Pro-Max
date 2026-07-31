using Avalonia.Data.Converters;

namespace v2rayN.Desktop.Converters;

/// <summary>
/// Paints every other row a faint accent so a long server list is easier to read across.
///
/// Driven by the row's own Index rather than by an <c>:nth-child</c> selector: the grid
/// recycles row containers as it scrolls, so a container's position among its siblings
/// does not track the position of the data in the list, and the banding comes out uneven.
/// </summary>
public class RowStripeConverter : IValueConverter
{
    private static readonly IBrush _stripe = new SolidColorBrush(Color.Parse("#F15A2B"), 0.06);
    private static readonly IBrush _plain = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index)
        {
            return index % 2 == 1 ? _stripe : _plain;
        }
        return _plain;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
