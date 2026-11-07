using System.Globalization;
using Avalonia.Data.Converters;

namespace GitWizardUI.Converters;

/// <summary>
/// Returns <c>true</c> when the bound <see cref="Enum"/> value has the flag named by the
/// ConverterParameter set. Drives the <c>.active</c> style class on the additive Filter sidebar
/// buttons, where several flags of the view model's <c>ActiveFilter</c> can be on at once.
/// </summary>
public class EnumHasFlagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Enum current
            && parameter is string name
            && Enum.TryParse(current.GetType(), name, out var flag)
            && (System.Convert.ToInt64(current, CultureInfo.InvariantCulture) & System.Convert.ToInt64(flag, CultureInfo.InvariantCulture)) != 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
