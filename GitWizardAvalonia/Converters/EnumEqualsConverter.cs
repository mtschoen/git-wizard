using System.Globalization;
using Avalonia.Data.Converters;

namespace GitWizardAvalonia.Converters;

/// <summary>
/// Returns <c>true</c> when the bound value's string form equals the ConverterParameter. Used to
/// drive the <c>.active</c> style class on the Filter / Group / Sort sidebar buttons by comparing
/// the view model's <c>Active{Filter,GroupMode,SortMode}</c> enum against each button's identity
/// (e.g. <c>ConverterParameter=Stale</c>). One converter serves all three enums since the
/// comparison is purely on the enum value name.
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
