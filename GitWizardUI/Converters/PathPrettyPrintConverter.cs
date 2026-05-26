using System.Globalization;
using Avalonia.Data.Converters;
using GitWizard;

namespace GitWizardUI.Converters;

public class PathPrettyPrintConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path)
            return GitWizardApi.PrettyPrintPath(path);
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
