using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace GitWizardUI.Converters;

/// <summary>
/// Converts a "left,top,right,bottom" string (as produced by the framework-agnostic
/// <c>RepositoryNodeViewModel.ItemPaddingString</c>) into an <see cref="Thickness"/>.
/// Avalonia bindings, unlike MAUI's, do not apply the target property's type converter, so
/// binding the string straight to <c>Padding</c> throws InvalidCastException per row on scroll.
/// </summary>
public class StringToThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text ? Thickness.Parse(text) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value;
}
