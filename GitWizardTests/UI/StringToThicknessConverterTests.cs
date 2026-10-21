using System.Globalization;
using Avalonia;
using GitWizardUI.Converters;

namespace GitWizardTests;

public class StringToThicknessConverterTests
{
    readonly StringToThicknessConverter _converter = new();

    [Test]
    public void Convert_PaddingString_ReturnsThickness()
    {
        var result = _converter.Convert(
            "1,2,3,4",
            typeof(Thickness),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.That(result, Is.EqualTo(new Thickness(1, 2, 3, 4)));
    }

    [Test]
    public void Convert_NonStringValue_ReturnsSameValue()
    {
        var value = new object();

        var result = _converter.Convert(
            value,
            typeof(Thickness),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.That(result, Is.SameAs(value));
    }

    [Test]
    public void ConvertBack_Value_ReturnsSameValue()
    {
        var value = new Thickness(5);

        var result = _converter.ConvertBack(
            value,
            typeof(string),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.That(result, Is.EqualTo(value));
    }
}
